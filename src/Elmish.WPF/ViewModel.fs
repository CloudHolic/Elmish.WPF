namespace Elmish.WPF

open System
open System.Dynamic
open System.Collections.Generic
open System.Collections.ObjectModel
open System.ComponentModel
open System.Windows
open Microsoft.Extensions.Logging

open Elmish


type internal OneWayBinding<'model, 'a when 'a : equality> = {
  OneWayData: OneWayData<'model, 'a>
}

type internal OneWayLazyBinding<'model, 'a, 'b> = {
  OneWayLazyData: OneWayLazyData<'model, 'a, 'b>
}

type internal OneWaySeqBinding<'model, 'a, 'b, 'id> = {
  Get: 'model -> 'a
  Equals: 'a -> 'a -> bool
  Map: 'a -> 'b seq
  GetId: 'b -> 'id
  ItemEquals: 'b -> 'b -> bool
  Values: ObservableCollection<'b>
}

type internal TwoWayBinding<'model, 'msg, 'a> = {
  Get: 'model -> 'a
  Set: 'a -> 'model -> unit
}

type internal TwoWayValidateBinding<'model, 'msg, 'a> = {
  Get: 'model -> 'a
  Set: 'a -> 'model -> unit
  Validate: 'model -> string voption
}

type internal CmdBinding<'model, 'msg> = {
  Cmd: Command
  CanExec: 'model -> bool
}

type internal SubModelBinding<'model, 'msg, 'bindingModel, 'bindingMsg> = {
  GetModel: 'model -> 'bindingModel voption
  GetBindings: unit -> Binding<'bindingModel, 'bindingMsg> list
  ToMsg: 'model -> 'bindingMsg -> 'msg
  Sticky: bool
  Vm: ViewModel<'bindingModel, 'bindingMsg> voption ref
}

and internal SubModelWinBinding<'model, 'msg, 'bindingModel, 'bindingMsg> = {
  GetState: 'model -> WindowState<'bindingModel>
  GetBindings: unit -> Binding<'bindingModel, 'bindingMsg> list
  ToMsg: 'model -> 'bindingMsg -> 'msg
  GetWindow: 'model -> Dispatch<'msg> -> Window
  IsModal: bool
  OnCloseRequested: 'model -> unit
  WinRef: WeakReference<Window>
  PreventClose: bool ref
  VmWinState: WindowState<ViewModel<'bindingModel, 'bindingMsg>> ref
}

and internal SubModelSeqBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'id> = {
  GetModels: 'model -> 'bindingModel seq
  GetId: 'bindingModel -> 'id
  GetBindings: unit -> Binding<'bindingModel, 'bindingMsg> list
  ToMsg: 'model -> 'id * 'bindingMsg -> 'msg
  Vms: ObservableCollection<ViewModel<'bindingModel, 'bindingMsg>>
}

and internal SubModelSelectedItemBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'id> = {
  Get: 'model -> 'id voption
  Set: 'id voption -> 'model -> unit
  SubModelSeqBinding: SubModelSeqBinding<'model, 'msg, obj, obj, obj>
}

and internal CachedBinding<'model, 'msg, 'value> = {
  Binding: VmBinding<'model, 'msg>
  Cache: 'value option ref
}


/// Represents all necessary data used in an active binding.
and internal VmBinding<'model, 'msg> =
  | OneWay of OneWayBinding<'model, obj>
  | OneWayLazy of OneWayLazyBinding<'model, obj, obj>
  | OneWaySeq of OneWaySeqBinding<'model, obj, obj, obj>
  | TwoWay of TwoWayBinding<'model, 'msg, obj>
  | TwoWayValidate of TwoWayValidateBinding<'model, 'msg, obj>
  | Cmd of CmdBinding<'model, 'msg>
  | CmdParam of cmd: Command
  | SubModel of SubModelBinding<'model, 'msg, obj, obj>
  | SubModelWin of SubModelWinBinding<'model, 'msg, obj, obj>
  | SubModelSeq of SubModelSeqBinding<'model, 'msg, obj, obj, obj>
  | SubModelSelectedItem of SubModelSelectedItemBinding<'model, 'msg, obj, obj, obj>
  | Cached of CachedBinding<'model, 'msg, obj>


and [<AllowNullLiteral>] internal ViewModel<'model, 'msg>
      ( initialModel: 'model,
        dispatch: 'msg -> unit,
        bindings: Binding<'model, 'msg> list,
        performanceLogThresholdMs: int,
        propNameChain: string,
        log: ILogger,
        logPerformance: ILogger)
      as this =
  inherit DynamicObject()

  let mutable currentModel = initialModel

  let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()
  let errorsChanged = DelegateEvent<EventHandler<DataErrorsChangedEventArgs>>()

  /// Error messages keyed by property name.
  let errors = Dictionary<string, string>()


  let withCaching b = Cached { Binding = b; Cache = ref None }


  let getPropChainFor bindingName =
    sprintf "%s.%s" propNameChain bindingName

  let getPropChainForItem collectionBindingName itemId =
    sprintf "%s.%s.%s" propNameChain collectionBindingName itemId

  let notifyPropertyChanged propName =
    log.LogTrace("[{BindingNameChain}] PropertyChanged \"{BindingName}\"", propNameChain, propName)
    propertyChanged.Trigger(this, PropertyChangedEventArgs propName)

  let raiseCanExecuteChanged (cmd: Command) =
    cmd.RaiseCanExecuteChanged ()

  let setError error propName =
    match errors.TryGetValue propName with
    | true, err when err = error -> ()
    | _ ->
        log.LogTrace("[{BindingNameChain}] ErrorsChanged \"{BindingName}\"", propNameChain, propName)
        errors.[propName] <- error
        errorsChanged.Trigger([| box this; box <| DataErrorsChangedEventArgs propName |])

  let removeError propName =
    if errors.Remove propName then
      log.LogTrace("[{BindingNameChain}] ErrorsChanged \"{BindingName}\"", propNameChain, propName)
      errorsChanged.Trigger([| box this; box <| DataErrorsChangedEventArgs propName |])

  let rec updateValidationError model name = function
    | TwoWayValidate { Validate = validate } ->
        match validate model with
        | ValueNone -> removeError name
        | ValueSome error -> setError error name
    | OneWay _
    | OneWayLazy _
    | OneWaySeq _
    | TwoWay _
    | Cmd _
    | CmdParam _
    | SubModel _
    | SubModelWin _
    | SubModelSeq _
    | SubModelSelectedItem _ -> ()
    | Cached b -> updateValidationError model name b.Binding

  let measure name callName f =
    if not <| logPerformance.IsEnabled(LogLevel.Trace) then f
    else
      fun x ->
        let sw = System.Diagnostics.Stopwatch.StartNew ()
        let r = f x
        sw.Stop ()
        if sw.ElapsedMilliseconds >= int64 performanceLogThresholdMs then
          logPerformance.LogTrace("[{BindingNameChain}] {CallName} ({Elapsed}ms): {MeasureName}", propNameChain, callName, sw.ElapsedMilliseconds, name)
        r

  let measure2 name callName f =
    if not <| logPerformance.IsEnabled(LogLevel.Trace) then f
    else fun x -> measure name callName (f x)

  let showNewWindow
      (winRef: WeakReference<Window>)
      (getWindow: 'model -> Dispatch<'msg> -> Window)
      dataContext
      isDialog
      (onCloseRequested: 'model -> unit)
      (preventClose: bool ref)
      initialVisibility =
    let win = getWindow currentModel dispatch
    winRef.SetTarget win
    win.Dispatcher.Invoke(fun () ->
      let guiCtx = System.Threading.SynchronizationContext.Current
      async {
        win.DataContext <- dataContext
        win.Closing.Add(fun ev ->
          ev.Cancel <- !preventClose
          async {
            do! Async.SwitchToThreadPool()
            onCloseRequested currentModel
          } |> Async.StartImmediate
        )
        do! Async.SwitchToContext guiCtx
        if isDialog
        then win.ShowDialog () |> ignore
        else win.Visibility <- initialVisibility
      } |> Async.StartImmediate
    )

  let initializeBinding name bindingData getInitializedBindingByName =
    let measure x = x |> measure name
    let measure2 x = x |> measure2 name
    match bindingData with
    | OneWayData d ->
        { OneWayData = d |> OneWayData.measureFunctions measure }
        |> OneWay
        |> Some 
    | OneWayLazyData d ->
        { OneWayLazyData = d |> OneWayLazyData.measureFunctions measure measure measure2 }
        |> OneWayLazy
        |> withCaching
        |> Some
    | OneWaySeqLazyData d ->
        let d = d |> OneWaySeqLazyData.measureFunctions measure measure measure2 measure measure2
        let values = ObservableCollection(initialModel |> d.Get |> d.Map)
        Some <| OneWaySeq {
          Get = d.Get
          Map = d.Map
          Equals = d.Equals
          GetId = d.GetId
          ItemEquals = d.ItemEquals
          Values = values }
    | TwoWayData d ->
        let d = d |> TwoWayData.measureFunctions measure measure
        Some <| TwoWay {
          Get = d.Get
          Set = fun obj m -> d.Set obj m |> dispatch }
    | TwoWayValidateData d ->
        let d = d |> TwoWayValidateData.measureFunctions measure measure measure
        Some <| TwoWayValidate {
          Get = d.Get
          Set = fun obj m -> d.Set obj m |> dispatch
          Validate = d.Validate }
    | CmdData d ->
        let d = d |> CmdData.measureFunctions measure measure
        let execute _ = d.Exec currentModel |> ValueOption.iter dispatch
        let canExecute _ = d.CanExec currentModel
        Some <| Cmd {
          Cmd = Command(execute, canExecute, false)
          CanExec = d.CanExec }
    | CmdParamData d ->
        let d = d |> CmdParamData.measureFunctions measure2 measure2
        let execute param = d.Exec param currentModel |> ValueOption.iter dispatch
        let canExecute param = d.CanExec param currentModel
        Some <| CmdParam (Command(execute, canExecute, d.AutoRequery))
    | SubModelData d ->
        let d = d |> SubModelData.measureFunctions measure measure measure2
        let toMsg = fun msg -> d.ToMsg currentModel msg
        match d.GetModel initialModel with
        | ValueNone ->
            Some <| SubModel {
              GetModel = d.GetModel
              GetBindings = d.GetBindings
              ToMsg = d.ToMsg
              Sticky = d.Sticky
              Vm = ref ValueNone }
        | ValueSome m ->
            let chain = getPropChainFor name
            let vm = ViewModel(m, toMsg >> dispatch, d.GetBindings (), performanceLogThresholdMs, chain, log, logPerformance)
            Some <| SubModel {
              GetModel = d.GetModel
              GetBindings = d.GetBindings
              ToMsg = d.ToMsg
              Sticky = d.Sticky
              Vm = ref <| ValueSome vm }
    | SubModelWinData d ->
        let d = d |> SubModelWinData.measureFunctions measure measure measure2
        let toMsg = fun msg -> d.ToMsg currentModel msg
        let onCloseRequested = fun m -> m |> d.OnCloseRequested |> ValueOption.iter dispatch
        match d.GetState initialModel with
        | WindowState.Closed ->
            Some <| SubModelWin {
              GetState = d.GetState
              GetBindings = d.GetBindings
              ToMsg = d.ToMsg
              GetWindow = d.GetWindow
              IsModal = d.IsModal
              OnCloseRequested = onCloseRequested
              WinRef = WeakReference<_>(null)
              PreventClose = ref true
              VmWinState = ref WindowState.Closed
            }
        | WindowState.Hidden m ->
            let chain = getPropChainFor name
            let vm = ViewModel(m, toMsg >> dispatch, d.GetBindings (), performanceLogThresholdMs, chain, log, logPerformance)
            let winRef = WeakReference<_>(null)
            let preventClose = ref true
            log.LogTrace("[{BindingNameChain}] Creating hidden window", chain)
            showNewWindow
              winRef d.GetWindow vm d.IsModal onCloseRequested
              preventClose Visibility.Hidden
            Some <| SubModelWin {
              GetState = d.GetState
              GetBindings = d.GetBindings
              ToMsg = d.ToMsg
              GetWindow = d.GetWindow
              IsModal = d.IsModal
              OnCloseRequested = onCloseRequested
              WinRef = winRef
              PreventClose = preventClose
              VmWinState = ref <| WindowState.Hidden vm
            }
        | WindowState.Visible m ->
            let chain = getPropChainFor name
            let vm = ViewModel(m, toMsg >> dispatch, d.GetBindings (), performanceLogThresholdMs, chain, log, logPerformance)
            let winRef = WeakReference<_>(null)
            let preventClose = ref true
            log.LogTrace("[{BindingNameChain}] Creating and opening window", chain)
            showNewWindow
              winRef d.GetWindow vm d.IsModal onCloseRequested
              preventClose Visibility.Visible
            Some <| SubModelWin {
              GetState = d.GetState
              GetBindings = d.GetBindings
              ToMsg = d.ToMsg
              GetWindow = d.GetWindow
              IsModal = d.IsModal
              OnCloseRequested = onCloseRequested
              WinRef = winRef
              PreventClose = preventClose
              VmWinState = ref <| WindowState.Visible vm
            }
    | SubModelSeqData d ->
        let d = d |> SubModelSeqData.measureFunctions measure measure measure measure2
        let toMsg = fun msg -> d.ToMsg currentModel msg
        let vms =
          d.GetModels initialModel
          |> Seq.map (fun m ->
               let chain = getPropChainForItem name (d.GetId m |> string)
               ViewModel(m, (fun msg -> toMsg (d.GetId m, msg) |> dispatch), d.GetBindings (), performanceLogThresholdMs, chain, log, logPerformance)
          )
          |> ObservableCollection
        Some <| SubModelSeq {
          GetModels = d.GetModels
          GetId = d.GetId
          GetBindings = d.GetBindings
          ToMsg = d.ToMsg
          Vms = vms }
    | SubModelSelectedItemData d ->
        let d = d |> SubModelSelectedItemData.measureFunctions measure measure2
        match getInitializedBindingByName d.SubModelSeqBindingName with
        | Some (SubModelSeq b) ->
          SubModelSelectedItem {
            Get = d.Get
            Set = fun obj m -> d.Set obj m |> dispatch
            SubModelSeqBinding = b
          } |> withCaching |> Some
        | _ ->
          log.LogError("subModelSelectedItem binding referenced binding '{SubModelSeqBindingName}', but no compatible binding was found with that name", d.SubModelSeqBindingName)
          None

  let bindings =
    log.LogTrace("[{BindingNameChain}] Initializing bindings", propNameChain)
    let dict = Dictionary<string, VmBinding<'model, 'msg>>(bindings.Length)
    let dictAsFunc name =
      match dict.TryGetValue name with
      | true, b -> Some b
      | _ -> None
    let sortedBindings = bindings |> List.sortWith Binding.subModelSelectedItemLast
    for b in sortedBindings do
      if dict.ContainsKey b.Name then
        log.LogError("Binding name '{BindingName}' is duplicated. Only the first occurrence will be used.", b.Name)
      else
        initializeBinding b.Name b.Data dictAsFunc
        |> Option.iter (fun binding ->
          dict.Add(b.Name, binding)
          updateValidationError initialModel b.Name binding)
    dict :> IReadOnlyDictionary<string, VmBinding<'model, 'msg>>

  /// Updates the binding value (for relevant bindings) and returns a value
  /// indicating whether to trigger PropertyChanged for this binding
  let rec updateValue bindingName newModel = function
    | OneWay { OneWayData = d } -> d.UpdateValue(currentModel, newModel)
    | TwoWay { Get = get }
    | TwoWayValidate { Get = get } ->
        get currentModel <> get newModel
    | OneWayLazy { OneWayLazyData = d } -> d.UpdateValue(currentModel, newModel)
    | OneWaySeq b ->
        let intermediate = b.Get newModel
        if not <| b.Equals intermediate (b.Get currentModel) then
          let create v _ = v
          let update oldVal newVal oldIdx =
            if not (b.ItemEquals newVal oldVal) then
              b.Values.[oldIdx] <- newVal
          let newVals = intermediate |> b.Map |> Seq.toArray
          elmStyleMerge b.GetId b.GetId create update b.Values newVals
        false
    | Cmd _
    | CmdParam _ ->
        false
    | SubModel b ->
      match !b.Vm, b.GetModel newModel with
      | ValueNone, ValueNone -> false
      | ValueSome _, ValueNone ->
          if b.Sticky then false
          else
            b.Vm := ValueNone
            true
      | ValueNone, ValueSome m ->
          let toMsg1 = fun msg -> b.ToMsg currentModel msg
          b.Vm := ValueSome <| ViewModel(m, toMsg1 >> dispatch, b.GetBindings (), performanceLogThresholdMs, getPropChainFor bindingName, log, logPerformance)
          true
      | ValueSome vm, ValueSome m ->
          vm.UpdateModel m
          false
    | SubModelWin b ->
        let winPropChain = getPropChainFor bindingName

        let close () =
          b.PreventClose := false
          match b.WinRef.TryGetTarget () with
          | false, _ ->
              log.LogError("[{BindingNameChain}] Attempted to close window, but did not find window reference", winPropChain)
          | true, w ->
              log.LogTrace("[{BindingNameChain}] Closing window", winPropChain)
              b.WinRef.SetTarget null
              w.Dispatcher.Invoke(fun () -> w.Close ())
          b.WinRef.SetTarget null

        let hide () =
          match b.WinRef.TryGetTarget () with
          | false, _ ->
              log.LogError("[{BindingNameChain}] Attempted to hide window, but did not find window reference", winPropChain)
          | true, w ->
              log.LogTrace("[{BindingNameChain}] Hiding window", winPropChain)
              w.Dispatcher.Invoke(fun () -> w.Visibility <- Visibility.Hidden)

        let showHidden () =
          match b.WinRef.TryGetTarget () with
          | false, _ ->
              log.LogError("[{BindingNameChain}] Attempted to show existing hidden window, but did not find window reference", winPropChain)
          | true, w ->
              log.LogTrace("[{BindingNameChain}] Showing existing hidden window", winPropChain)
              w.Dispatcher.Invoke(fun () -> w.Visibility <- Visibility.Visible)

        let showNew vm initialVisibility =
          b.PreventClose := true
          showNewWindow
            b.WinRef b.GetWindow vm b.IsModal b.OnCloseRequested
            b.PreventClose initialVisibility

        let newVm model =
          let toMsg1 = fun msg -> b.ToMsg currentModel msg
          ViewModel(model, toMsg1 >> dispatch, b.GetBindings (), performanceLogThresholdMs, getPropChainFor bindingName, log, logPerformance)

        match !b.VmWinState, b.GetState newModel with
        | WindowState.Closed, WindowState.Closed ->
            false
        | WindowState.Hidden _, WindowState.Closed
        | WindowState.Visible _, WindowState.Closed ->
            close ()
            b.VmWinState := WindowState.Closed
            true
        | WindowState.Closed, WindowState.Hidden m ->
            let vm = newVm m
            log.LogTrace("[{BindingNameChain}] Creating hidden window", winPropChain)
            showNew vm Visibility.Hidden
            b.VmWinState := WindowState.Hidden vm
            true
        | WindowState.Hidden vm, WindowState.Hidden m ->
            vm.UpdateModel m
            false
        | WindowState.Visible vm, WindowState.Hidden m ->
            hide ()
            vm.UpdateModel m
            b.VmWinState := WindowState.Hidden vm
            false
        | WindowState.Closed, WindowState.Visible m ->
            let vm = newVm m
            log.LogTrace("[{BindingNameChain}] Creating and opening window", winPropChain)
            showNew vm Visibility.Visible
            b.VmWinState := WindowState.Visible vm
            true
        | WindowState.Hidden vm, WindowState.Visible m ->
            vm.UpdateModel m
            showHidden ()
            b.VmWinState := WindowState.Visible vm
            false
        | WindowState.Visible vm, WindowState.Visible m ->
            vm.UpdateModel m
            false
    | SubModelSeq b ->
        let getTargetId (vm: ViewModel<_, _>) = b.GetId vm.CurrentModel
        let create m id = 
          let toMsg1 = fun msg -> b.ToMsg currentModel msg
          let chain = getPropChainForItem bindingName (id |> string)
          ViewModel(m, (fun msg -> toMsg1 (id, msg) |> dispatch), b.GetBindings (), performanceLogThresholdMs, chain, log, logPerformance)
        let update (vm: ViewModel<_, _>) m _ = vm.UpdateModel m
        let newSubModels = newModel |> b.GetModels |> Seq.toArray
        elmStyleMerge b.GetId getTargetId create update b.Vms newSubModels
        false
    | SubModelSelectedItem b ->
        b.Get newModel <> b.Get currentModel
    | Cached b ->
        let valueChanged = updateValue bindingName newModel b.Binding
        if valueChanged then
          b.Cache := None
        valueChanged

  /// Returns the command associated with a command binding if the command's
  /// CanExecuteChanged should be triggered.
  let rec getCmdIfCanExecChanged newModel = function
    | OneWay _
    | OneWayLazy _
    | OneWaySeq _
    | TwoWay _
    | TwoWayValidate _
    | SubModel _
    | SubModelWin _
    | SubModelSeq _
    | SubModelSelectedItem _ ->
        None
    | Cmd { Cmd = cmd; CanExec = canExec } ->
        if canExec newModel = canExec currentModel
        then None
        else Some cmd
    | CmdParam cmd ->
        Some cmd
    | Cached b -> getCmdIfCanExecChanged newModel b.Binding

  let rec tryGetMember model = function
    | OneWay { OneWayData = d } -> d.TryGetMember model
    | TwoWay { Get = get }
    | TwoWayValidate { Get = get } ->
        get model
    | OneWayLazy { OneWayLazyData = d } -> d.TryGetMember model
    | OneWaySeq { Values = vals } ->
        box vals
    | Cmd { Cmd = cmd }
    | CmdParam cmd ->
        box cmd
    | SubModel { Vm = vm } -> !vm |> ValueOption.toObj |> box
    | SubModelWin { VmWinState = vm } ->
        match !vm with
        | WindowState.Closed -> null
        | WindowState.Hidden vm | WindowState.Visible vm -> box vm
    | SubModelSeq { Vms = vms } -> box vms
    | SubModelSelectedItem b ->
        let selectedId = b.Get model
        let selected =
          b.SubModelSeqBinding.Vms 
          |> Seq.tryFind (fun (vm: ViewModel<obj, obj>) ->
            selectedId = ValueSome (b.SubModelSeqBinding.GetId vm.CurrentModel))
        log.LogTrace(
          "[{BindingNameChain}] Setting selected VM to {SubModelId}",
          propNameChain,
          (selected |> Option.map (fun vm -> b.SubModelSeqBinding.GetId vm.CurrentModel))
        )
        selected |> Option.toObj |> box
    | Cached b ->
        match !b.Cache with
        | Some v -> v
        | None ->
            let v = tryGetMember model b.Binding
            b.Cache := Some v
            v

  let rec trySetMember model (value: obj) = function
    | TwoWay { Set = set }
    | TwoWayValidate { Set = set } ->
        set value model
        true
    | SubModelSelectedItem b ->
        let id =
          (value :?> ViewModel<obj, obj>)
          |> ValueOption.ofObj
          |> ValueOption.map (fun vm -> b.SubModelSeqBinding.GetId vm.CurrentModel)
        b.Set id model
        true
    | Cached b ->
        let successful = trySetMember model value b.Binding
        if successful then
          b.Cache := None  // TODO #185: write test
        successful
    | OneWay _
    | OneWayLazy _
    | OneWaySeq _
    | Cmd _
    | CmdParam _
    | SubModel _
    | SubModelWin _
    | SubModelSeq _ ->
        false

  member __.CurrentModel : 'model = currentModel

  member __.UpdateModel (newModel: 'model) : unit =
    let propsToNotify =
      bindings
      |> Seq.filter (fun (Kvp (name, binding)) -> updateValue name newModel binding)
      |> Seq.map Kvp.key
      |> Seq.toList
    let cmdsToNotify =
      bindings
      |> Seq.choose (Kvp.value >> getCmdIfCanExecChanged newModel)
      |> Seq.toList
    currentModel <- newModel
    propsToNotify |> List.iter notifyPropertyChanged
    cmdsToNotify |> List.iter raiseCanExecuteChanged
    for Kvp (name, binding) in bindings do
      updateValidationError currentModel name binding

  override __.TryGetMember (binder, result) =
    log.LogTrace("[{BindingNameChain}] TryGetMember {BindingName}", propNameChain, binder.Name)
    match bindings.TryGetValue binder.Name with
    | false, _ ->
        log.LogError("[{BindingNameChain}] TryGetMember FAILED: Property {BindingName} doesn't exist", propNameChain, binder.Name)
        false
    | true, binding ->
        result <- tryGetMember currentModel binding
        true

  override __.TrySetMember (binder, value) =
    log.LogTrace("[{BindingNameChain}] TrySetMember {BindingName}", propNameChain, binder.Name)
    match bindings.TryGetValue binder.Name with
    | false, _ ->
        log.LogError("[{BindingNameChain}] TrySetMember FAILED: Property {BindingName} doesn't exist", propNameChain, binder.Name)
        false
    | true, binding ->
        let success = trySetMember currentModel value binding
        if not success then
          log.LogError("[{BindingNameChain}] TrySetMember FAILED: Binding {BindingName} is read-only", propNameChain, binder.Name)
        success


  interface INotifyPropertyChanged with
    [<CLIEvent>]
    member __.PropertyChanged = propertyChanged.Publish

  interface INotifyDataErrorInfo with
    [<CLIEvent>]
    member __.ErrorsChanged = errorsChanged.Publish
    member __.HasErrors =
      errors.Count > 0
    member __.GetErrors propName =
      log.LogTrace("[{BindingNameChain}] GetErrors {BindingName}", propNameChain, (propName |> Option.ofObj |> Option.defaultValue "<null>"))
      match errors.TryGetValue propName with
      | true, err -> upcast [err]
      | false, _ -> upcast []

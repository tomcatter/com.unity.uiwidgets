/*using System;
using System.Collections.Generic;
using Unity.UIWidgets.async2;
using Unity.UIWidgets.DevTools.inspector;
using Unity.UIWidgets.foundation;
using Unity.UIWidgets.rendering;
using UnityEngine;
using Object = System.Object;

namespace Unity.UIWidgets.DevTools.inspector
{
    public class inspector_service
    {
      public static readonly List<string> inspectorLibraryUriCandidates = new List<string>{
      "package:flutter/src/widgets/widget_inspector.dart",
      "package:flutter_web/src/widgets/widget_inspector.dart",
      };
      
      bool _inspectorDependenciesLoaded = false;
      
      Future ensureInspectorServiceDependencies() {
        if (_inspectorDependenciesLoaded) {
          return Future.value();
        }
        // TODO(jacobr): consider also loading common icons needed by the inspector
        // to avoid flicker on icon load.
        _inspectorDependenciesLoaded = true;
        return Future.value();
      }
    }
    

  public class RegistrableServiceExtension {
    public RegistrableServiceExtension(string name)
    {
      this.name = name;
    }

    public readonly string name;
    
    public static readonly RegistrableServiceExtension getLayoutExplorerNode =
        new RegistrableServiceExtension("getLayoutExplorerNode");
    public static readonly RegistrableServiceExtension setFlexFit = new RegistrableServiceExtension("setFlexFit");
    public static readonly RegistrableServiceExtension setFlexFactor = new RegistrableServiceExtension("setFlexFactor");
    public static readonly RegistrableServiceExtension setFlexProperties =
        new RegistrableServiceExtension("setFlexProperties");

    public static readonly RegistrableServiceExtension getPubRootDirectories =
        new RegistrableServiceExtension("getPubRootDirectories");
  }


public class InspectorService : DisposableController, AutoDisposeControllerMixin {
  public InspectorService(
    VmService vmService,
    HashSet<InspectorServiceClient> inspectorLibrary,
    HashSet<string> supportedServiceMethods
  )
  {
    this.vmService = vmService;
    this.inspectorLibrary = inspectorLibrary;
    this.supportedServiceMethods = supportedServiceMethods;
    
    clients = new HashSet<InspectorServiceClient>(); 
    // Note: We do not need to listen to event history here because the
    // inspector uses a separate API to get the current inspector selection.
    autoDispose(
        vmService.onExtensionEvent.listen(onExtensionVmServiceRecieved));
    autoDispose(vmService.onDebugEvent.listen(onDebugVmServiceReceived));

    autoDispose(serviceManager.isolateManager
        .getSelectedIsolate((IsolateRef flutterIsolate) => {
      // Any time we have a new isolate it means the previous isolate stopped.
      _onIsolateStopped();
    }));
  }

  static int nextGroupId = 0;

  public readonly VmService vmService;
  public readonly HashSet<InspectorServiceClient> clients;
  public readonly EvalOnDartLibrary inspectorLibrary;
  public readonly HashSet<string> supportedServiceMethods;

  /// [ensureInspectorServiceDependencies] must be called before this method is
  /// called.
  static Future<ObjectGroup> createGroup(
    VmService vmService,
    String groupName,
  ) async {
    assert(_inspectorDependenciesLoaded);
    return (await create(vmService)).createObjectGroup(groupName);
  }

  static Future<InspectorService> create(VmService vmService) async {
    assert(_inspectorDependenciesLoaded);
    assert(serviceManager.hasConnection);
    assert(serviceManager.service != null);
    final inspectorLibrary = EvalOnDartLibrary(
      inspectorLibraryUriCandidates,
      vmService,
    );

    final libraryRef = await inspectorLibrary.libraryRef.catchError(
      (_) => throw FlutterInspectorLibraryNotFound(),
      test: (e) => e is LibraryNotFound,
    );
    final libraryFuture = inspectorLibrary.getLibrary(libraryRef, null);
    final library = await libraryFuture;
    Future<Set<String>> lookupFunctionNames() async {
      for (ClassRef classRef in library.classes) {
        if ('WidgetInspectorService' == classRef.name) {
          final classObj = await inspectorLibrary.getClass(classRef, null);
          final functionNames = <String>{};
          for (FuncRef funcRef in classObj.functions) {
            functionNames.add(funcRef.name);
          }
          return functionNames;
        }
      }
      // WidgetInspectorService is not available. Either this is not a Flutter
      // application or it is running in profile mode.
      return null;
    }

    final supportedServiceMethods = await lookupFunctionNames();
    if (supportedServiceMethods == null) return null;
    return InspectorService(
      vmService,
      inspectorLibrary,
      supportedServiceMethods,
    );
  }

  void _onIsolateStopped() {
    // Clear data that is obsolete on an isolate restart.
    _currentSelection = null;
    _cachedSelectionGroups?.clear(true);
    _expectedSelectionChanges.clear();
  }

  /// Map from InspectorInstanceRef to list of timestamps when a selection
  /// change to that ref was triggered by this application.
  ///
  /// This is needed to handle the case where we may send multiple selection
  /// change notifications to the device before we get a notification back that
  /// the selection has actually changed. Without this fix it was rare but
  /// possible to trigger an infinite loop ping-ponging back and forth between
  /// selecting two different nodes in the inspector tree if the selection was
  /// changed more rapidly than the running flutter app could update.
  final Map<InspectorInstanceRef, List<int>> _expectedSelectionChanges = {};

  /// Maximum time in milliseconds that we ever expect it will take for a
  /// selection change to apply.
  ///
  /// In general this heuristic based time should not matter but we keep it
  /// anyway so that in the unlikely event that package:flutter changes and we
  /// do not received all of the selection notification events we expect, we
  /// will not be impacted if there is at least the following delay between
  /// when selection was set to exactly the same location by both the on device
  /// inspector and DevTools.
  static const _maxTimeDelaySelectionNotification = 5000;

  void _trackClientSelfTriggeredSelection(InspectorInstanceRef ref) {
    _expectedSelectionChanges
        .putIfAbsent(ref, () => [])
        .add(DateTime.now().millisecondsSinceEpoch);
  }

  /// Returns whether the selection change was originally triggered by this
  /// application.
  ///
  /// This method is needed to avoid a race condition when there is a queue of
  /// inspector selection changes due to extremely rapidly navigating through
  /// the inspector tree such as when using the keyboard to navigate.
  bool _isClientTriggeredSelectionChange(InspectorInstanceRef ref) {
    // TODO(jacobr): once https://github.com/flutter/flutter/issues/39366 is
    // fixed in all versions of flutter we support, remove this logic and
    // determine the source of the inspector selection change directly from the
    // inspector selection changed event.
    final currentTime = DateTime.now().millisecondsSinceEpoch;
    if (ref != null) {
      if (_expectedSelectionChanges.containsKey(ref)) {
        final times = _expectedSelectionChanges.remove(ref);
        while (times.isNotEmpty) {
          final time = times.removeAt(0);
          if (time + _maxTimeDelaySelectionNotification >= currentTime) {
            // We triggered this selection change ourselves. This logic would
            // work fine without the timestamps for the typical case but we use
            // the timestamps to be safe in case there is a bug and selection
            // change events were somehow lost.
            return true;
          }
        }
      }
    }
    return false;
  }

  ValueListenable<List<String>> get rootDirectories => _rootDirectories;
  final ValueNotifier<List<String>> _rootDirectories = ValueNotifier([]);

  @visibleForTesting
  Set<String> get rootPackages => _rootPackages;
  Set<String> _rootPackages;

  @visibleForTesting
  List<String> get rootPackagePrefixes => _rootPackagePrefixes;
  List<String> _rootPackagePrefixes;

  Future<void> _onRootDirectoriesChanged(List<String> directories) async {
    _rootDirectories.value = directories;
    _rootPackages = {};
    _rootPackagePrefixes = [];
    for (var directory in directories) {
      // TODO(jacobr): add an API to DDS to provide the actual mapping to and
      // from absolute file paths to packages instead of having to guess it
      // here.
      assert(!directory.startsWith('package:'));

      final parts =
          directory.split('/').where((element) => element.isNotEmpty).toList();
      final libIndex = parts.lastIndexOf('lib');
      final path = libIndex > 0 ? parts.sublist(0, libIndex) : parts;
      // Special case handling of bazel packages.
      final google3Index = path.lastIndexOf('google3');
      if (google3Index != -1 && google3Index + 1 < path.length) {
        var packageParts = path.sublist(google3Index + 1);
        // A well formed third_party dart package should be in a directory of
        // the form
        // third_party/dart/packageName                    (package:packageName)
        // or
        // third_party/dart_src/long/package/name    (package:long.package.name)
        // so its path should be at minimum depth 3.
        const minThirdPartyPathDepth = 3;
        if (packageParts[0] == 'third_party' &&
            packageParts.length >= minThirdPartyPathDepth) {
          assert(packageParts[1] == 'dart' || packageParts[1] == 'dart_src');
          packageParts = packageParts.sublist(2);
        }
        final google3PackageName = packageParts.join('.');
        _rootPackages.add(google3PackageName);
        _rootPackagePrefixes.add(google3PackageName + '.');
      } else {
        _rootPackages.add(path.last);
      }
    }

    await _updateLocalClasses();
  }

  Future<void> _updateLocalClasses() async {
    localClasses.clear();
    if (_rootDirectories.value.isNotEmpty) {
      final isolate = inspectorLibrary.isolate;
      for (var libraryRef in isolate.libraries) {
        if (isLocalUri(libraryRef.uri)) {
          final Library library = await inspectorLibrary.service
              .getObject(isolate.id, libraryRef.id);
          for (var classRef in library.classes) {
            localClasses[classRef.name] = classRef;
          }
        }
      }
    }
  }

  @visibleForTesting
  bool isLocalUri(String rawUri) {
    final uri = Uri.parse(rawUri);
    if (uri.scheme != 'file' && uri.scheme != 'dart') {
      // package scheme or some other dart specific scheme.
      final packageName = uri.pathSegments.first;
      if (_rootPackages.contains(packageName)) return true;

      // This attempts to gracefully handle the bazel package case.
      return _rootPackagePrefixes
          .any((prefix) => packageName.startsWith(prefix));
    }
    for (var root in _rootDirectories.value) {
      if (root.endsWith(rawUri)) {
        return true;
      }
    }
    return false;
  }

  @visibleForTesting
  final Map<String, ClassRef> localClasses = {};

  bool isLocalClass(RemoteDiagnosticsNode node) {
    if (node.widgetRuntimeType == null) return false;
    // widgetRuntimeType may contain some generic type arguments which we need
    // to strip out. If widgetRuntimeType is "FooWidget<Bar>" then we are only
    // interested in the raw type "FooWidget".
    final rawType = node.widgetRuntimeType.split('<').first;
    return localClasses.containsKey(rawType);
  }

  /// As we aren't running from an IDE, we don't know exactly what the pub root
  /// directories are for the current project so we make a best guess if needed
  /// based on the the root directory of the first non artifical widget in the
  /// tree.
  Future<List<String>> inferPubRootDirectoryIfNeeded() async {
    final group = createObjectGroup('temp');
    List<String> directories = await group.getPubRootDirectories() ?? [];
    if (directories.isEmpty) {
      final directory = await inferPubRootDirectoryIfNeededHelper();
      if (directory != null) {
        directories = [directory];
      }
    }

    await _onRootDirectoriesChanged(directories);
    return directories;
  }

  Future<String> inferPubRootDirectoryIfNeededHelper() async {
    final group = createObjectGroup('temp');
    final root = await group.getRoot(FlutterTreeType.widget);

    if (root == null) {
      // No need to do anything as there isn't a valid tree (yet?).
      await group.dispose();
      return null;
    }
    List<RemoteDiagnosticsNode> children = await root.children;

    if (children?.isEmpty ?? true) {
      children = await group.getChildren(root.dartDiagnosticRef, false, null);
    }

    if (children?.isEmpty ?? true) {
      await group.dispose();
      return null;
    }
    final path = children.first.creationLocation?.path;
    if (path == null) {
      await group.dispose();
      return null;
    }
    // TODO(jacobr): it would be nice to use Isolate.rootLib similar to how
    // debugger.dart does but we are currently blocked by the
    // --track-widget-creation transformer generating absolute paths instead of
    // package:paths.
    // Once https://github.com/flutter/flutter/issues/26615 is fixed we will be
    // able to use package: paths. Temporarily all tools tracking widget
    // locations will need to support both path formats.
    // TODO(jacobr): use the list of loaded scripts to determine the appropriate
    // package root directory given that the root script of this project is in
    // this directory rather than guessing based on url structure.
    final parts = path.split('/');
    String pubRootDirectory;
    for (int i = parts.length - 1; i >= 0; i--) {
      final part = parts[i];
      if (part == 'lib' || part == 'web') {
        pubRootDirectory = parts.sublist(0, i).join('/');
        break;
      }

      if (part == 'packages') {
        pubRootDirectory = parts.sublist(0, i + 1).join('/');
        break;
      }
    }
    pubRootDirectory ??= (parts..removeLast()).join('/');

    await _setPubRootDirectories([pubRootDirectory]);
    await group.dispose();
    return pubRootDirectory;
  }

  /// Returns whether to use the Daemon API or the VM Service protocol directly.
  ///
  /// The VM Service protocol must be used when paused at a breakpoint as the
  /// Daemon API calls won't execute until after the current frame is done
  /// rendering.
  bool get useDaemonApi {
    return true;
    // TODO(jacobr): once there is a debugger, hook to it to determine whether
    // we are suspended.
    // return !app.isFlutterIsolateSuspended();
  }

  /// Use this method to write code that is backwards compatible with versions
  /// of Flutter that are too old to contain specific service methods.
  bool hasServiceMethod(String methodName) {
    return supportedServiceMethods.contains(methodName);
  }

  ObjectGroup createObjectGroup(String debugName) {
    return ObjectGroup(debugName, this);
  }

  @override
  void dispose() {
    inspectorLibrary.dispose();
    super.dispose();
  }

  Future<Object> forceRefresh() {
    final List<Future<Object>> futures = [];
    for (InspectorServiceClient client in clients) {
      try {
        futures.add(client.onForceRefresh());
      } catch (e) {
        log(e);
      }
    }
    return Future.wait(futures);
  }

  RemoteDiagnosticsNode _currentSelection;

  InspectorObjectGroupManager get _selectionGroups {
    return _cachedSelectionGroups ??=
        InspectorObjectGroupManager(this, 'selection');
  }

  InspectorObjectGroupManager _cachedSelectionGroups;

  void notifySelectionChanged() async {
    // The previous selection changed event is obsolete.
    _selectionGroups.cancelNext();
    final group = _selectionGroups.next;
    final pendingSelection = await group.getSelection(
      _currentSelection,
      FlutterTreeType.widget,
      isSummaryTree: false,
    );
    if (!group.disposed &&
        group == _selectionGroups.next &&
        !_isClientTriggeredSelectionChange(pendingSelection?.valueRef)) {
      _currentSelection = pendingSelection;
      assert(group == _selectionGroups.next);
      _selectionGroups.promoteNext();
      for (InspectorServiceClient client in clients) {
        client.onInspectorSelectionChanged();
      }
    }
  }

  void addClient(InspectorServiceClient client) {
    clients.add(client);
  }

  void onDebugVmServiceReceived(Event event) {
    if (event.kind == EventKind.kInspect) {
      // Update the UI in IntelliJ.
      notifySelectionChanged();
    }
  }

  void onExtensionVmServiceRecieved(Event e) {
    if ('Flutter.Frame' == e.extensionKind) {
      for (InspectorServiceClient client in clients) {
        try {
          client.onFlutterFrame();
        } catch (e) {
          log('Error handling frame event', error: e);
        }
      }
    }
  }

  /// If the widget tree is not ready, the application should wait for the next
  /// Flutter.Frame event before attempting to display the widget tree. If the
  /// application is ready, the next Flutter.Frame event may never come as no
  /// new frames will be triggered to draw unless something changes in the UI.
  Future<bool> isWidgetTreeReady() {
    return invokeBoolServiceMethodNoArgs('isWidgetTreeReady');
  }

  Future<bool> invokeBoolServiceMethodNoArgs(String methodName) async {
    if (useDaemonApi) {
      return await invokeServiceMethodDaemonNoGroupArgs(methodName) == true;
    } else {
      return (await invokeServiceMethodObservatoryNoGroup(methodName))
              ?.valueAsString ==
          'true';
    }
  }

  Future<bool> isWidgetCreationTracked() {
    return invokeBoolServiceMethodNoArgs('isWidgetCreationTracked');
  }

  Future<Object> invokeServiceMethodDaemonNoGroupArgs(String methodName,
      [List<String> args]) {
    final Map<String, Object> params = {};
    if (args != null) {
      for (int i = 0; i < args.length; ++i) {
        params['arg$i'] = args[i];
      }
    }
    return invokeServiceMethodDaemonNoGroup(methodName, params);
  }

  Future<void> setPubRootDirectories(List<String> rootDirectories) async {
    await _setPubRootDirectories(rootDirectories);
    await _onRootDirectoriesChanged(rootDirectories);
  }

  Future<void> _setPubRootDirectories(List<String> rootDirectories) {
    // No need to call this from a breakpoint.
    assert(useDaemonApi);
    return invokeServiceMethodDaemonNoGroupArgs(
      'setPubRootDirectories',
      rootDirectories,
    );
  }

  Future<List<String>> getPubRootDirectories() {
    // No need to call this from a breakpoint.
    assert(useDaemonApi);
    final result =
        invokeServiceMethodDaemonNoGroup('getPubRootDirectories', null);
    return result ?? [];
  }

  Future<InstanceRef> invokeServiceMethodObservatoryNoGroup(String methodName) {
    return inspectorLibrary
        .eval('WidgetInspectorService.instance.$methodName()', isAlive: null);
  }

  Future<Object> invokeServiceMethodDaemonNoGroup(
      String methodName, Map<String, Object> args) async {
    final callMethodName = 'ext.flutter.inspector.$methodName';
    if (!serviceManager.serviceExtensionManager
        .isServiceExtensionAvailable(callMethodName)) {
      final available = await serviceManager.serviceExtensionManager
          .waitForServiceExtensionAvailable(callMethodName);
      if (!available) return {'result': null};
    }

    final r = await vmService.callServiceExtension(
      callMethodName,
      isolateId: inspectorLibrary.isolateId,
      args: args,
    );
    final json = r.json;
    if (json['errorMessage'] != null) {
      throw Exception('$methodName -- ${json['errorMessage']}');
    }
    return json['result'];
  }

  void removeClient(InspectorServiceClient client) {
    clients.remove(client);
  }
}

/// Class managing a group of inspector objects that can be freed by
/// a single call to dispose().
/// After dispose is called, all pending requests made with the ObjectGroup
/// will be skipped. This means that clients should not have to write any
/// special logic to handle orphaned requests.
public class ObjectGroup {
  public ObjectGroup(
    string debugName,
    this.inspectorService,
  ) : groupName = '${debugName}_${InspectorService.nextGroupId}' {
    InspectorService.nextGroupId++;
  }

  /// Object group all objects in this arena are allocated with.
  final String groupName;
  final InspectorService inspectorService;
  bool disposed = false;

  EvalOnDartLibrary get inspectorLibrary => inspectorService.inspectorLibrary;

  bool get useDaemonApi => inspectorService.useDaemonApi;

  /// Once an ObjectGroup has been disposed, all methods returning
  /// DiagnosticsNode objects will return a placeholder dummy node and all methods
  /// returning lists or maps will return empty lists and all other methods will
  /// return null. Generally code should never call methods on a disposed object
  /// group but sometimes due to chained futures that can be difficult to avoid
  /// and it is simpler return an empty result that will be ignored anyway than to
  /// attempt carefully cancel futures.
  Future dispose() {
    var disposeComplete = invokeVoidServiceMethod("disposeGroup", groupName);
    disposed = true;
    return disposeComplete;
  }

  Future<T> nullIfDisposed<T>(Future<T> supplier()) {
    if (disposed) {
      return null;
    }
    return await supplier();
  }

  T nullValueIfDisposed<T>(T supplier()) {
    if (disposed) {
      return null;
    }

    return supplier();
  }

  void skipIfDisposed(void runnable()) {
    if (disposed) {
      return;
    }

    runnable();
  }

  Future<SourcePosition> getPropertyLocation(
    InstanceRef instanceRef, string name) async {
    Instance instance = await getInstance(instanceRef);
    if (instance == null || disposed) {
      return null;
    }
    return getPropertyLocationHelper(instance.classRef, name);
  }

  Future<SourcePosition> getPropertyLocationHelper(
      ClassRef classRef, String name) async {
    var clazz = await inspectorLibrary.getClass(classRef, this);
    foreach (FuncRef f in clazz.functions) {
      // TODO(pq): check for properties that match name.
      if (f.name == name) {
        var func = await inspectorLibrary.getFunc(f, this);
        var SourceLocation location = func.location;
        throw UnimplementedError(
            $"getSourcePosition not implemented. {location}");
//        return inspectorLibrary.getSourcePosition(
//            debugProcess, location.script, location.tokenPos, this);
      }
    }
    ClassRef superClass = clazz.superClass;
    return superClass == null
        ? null
        : getPropertyLocationHelper(superClass, name);
  }

  Future<RemoteDiagnosticsNode> getRoot(FlutterTreeType type) {
    // There is no excuse to call this method on a disposed group.
    D.assert(!disposed);
    switch (type) {
      case FlutterTreeType.widget:
        return getRootWidget();
      case FlutterTreeType.renderObject:
        return getRootRenderObject();
    }
    throw new Exception("Unexpected FlutterTreeType");
  }

  /// Invokes a static method on the WidgetInspectorService class passing in the specified
  /// arguments.
  ///
  /// Intent is we could refactor how the API is invoked by only changing this call.
  Future<InstanceRef> invokeServiceMethodObservatory(string methodName) {
    return invokeServiceMethodObservatory1(methodName, groupName);
  }

  Future<InstanceRef> invokeServiceMethodObservatory1(
      string methodName, string arg1) {
    return inspectorLibrary.eval(
      "WidgetInspectorService.instance.$methodName('$arg1')",
      isAlive: this
    );
  }

  Future<object> invokeServiceExtensionMethod(
    RegistrableServiceExtension extension,
    Dictionary<string, string> parameters
  ) async {
    var name = extension.name;
    var fullName = "ext.flutter.inspector.$name";
    if (!serviceManager.serviceExtensionManager
        .isServiceExtensionAvailable(fullName)) {
      // Wait until a service extension we know will be eventually available for
      // a Flutter app is loaded to avoid attempting to apply the polyfill
      // while the list of Flutter service extensions is really just being
      // registered on the device. This prevents pew in the app console about
      // trying to register service extensions multiple times.
      final regularExtensionsRegistered = await serviceManager
          .serviceExtensionManager
          .waitForServiceExtensionAvailable(
              'ext.flutter.inspector.isWidgetCreationTracked');
      if (disposed) return null;
      assert(regularExtensionsRegistered);
      if (!serviceManager.serviceExtensionManager
          .isServiceExtensionAvailable(fullName)) {
        await invokeInspectorPolyfill(this);
      }
      if (disposed) return null;
    }
    return invokeServiceMethodDaemonParams(name, parameters);
  }

  Future<object> invokeServiceMethodDaemon(string methodName,
      string objectGroup = null) {
    return invokeServiceMethodDaemonParams(
      methodName,
      "objectGroup": objectGroup ?? groupName
    );
  }

  Future<Object> invokeServiceMethodDaemonArg(
      String methodName, String arg, String objectGroup) {
    final args = {'objectGroup': objectGroup};
    if (arg != null) {
      args['arg'] = arg;
    }
    return invokeServiceMethodDaemonParams(methodName, args);
  }

  Future<Object> _callServiceExtension(
      String extension, Map<String, Object> args) {
    if (disposed) {
      return Future.value();
    }

    return inspectorLibrary.addRequest(this, () async {
      final r = await inspectorService.vmService.callServiceExtension(
        extension,
        isolateId: inspectorService.inspectorLibrary.isolateId,
        args: args,
      );
      if (disposed) return null;
      final json = r.json;
      if (json['errorMessage'] != null) {
        throw Exception('$extension -- ${json['errorMessage']}');
      }
      return json['result'];
    });
  }

  // All calls to invokeServiceMethodDaemon bottom out to this call.
  Future<object> invokeServiceMethodDaemonParams(
    string methodName,
    Dictionary<string, object> _params
  ) async {
    var callMethodName = $"ext.flutter.inspector.{methodName}";
    if (!serviceManager.serviceExtensionManager
        .isServiceExtensionAvailable(callMethodName)) {
      final available = await serviceManager.serviceExtensionManager
          .waitForServiceExtensionAvailable(callMethodName);
      if (!available) return null;
    }

    return await _callServiceExtension(callMethodName, params);
  }

  Future<Object> invokeServiceMethodDaemonInspectorRef(
      String methodName, InspectorInstanceRef arg) {
    return invokeServiceMethodDaemonArg(methodName, arg?.id, groupName);
  }

  Future<InstanceRef> invokeServiceMethodObservatoryInspectorRef(
      String methodName, InspectorInstanceRef arg) {
    return inspectorLibrary.eval(
        "WidgetInspectorService.instance.$methodName('${arg?.id}', '$groupName')",
        isAlive: this);
  }

  /// Call a service method passing in an observatory instance reference.
  ///
  /// This call is useful when receiving an 'inspect' event from the
  /// observatory and future use cases such as inspecting a Widget from a
  /// log window.
  ///
  /// This method will always need to use the observatory service as the input
  /// parameter is an Observatory InstanceRef..
  Future<InstanceRef> invokeServiceMethodOnRefObservatory(
      String methodName, InstanceRef arg) {
    if (arg == null) {
      return inspectorLibrary.eval(
        "WidgetInspectorService.instance.$methodName(null, '$groupName')",
        isAlive: this,
      );
    }
    return inspectorLibrary.eval(
      "WidgetInspectorService.instance.$methodName(arg1, '$groupName')",
      isAlive: this,
      scope: {'arg1': arg.id},
    );
  }

  Future<RemoteDiagnosticsNode> parseDiagnosticsNodeObservatory(
      FutureOr<InstanceRef> instanceRefFuture) async {
    return parseDiagnosticsNodeHelper(
        await instanceRefToJson(await instanceRefFuture));
  }

  /// Returns a Future with a Map of property names to Observatory
  /// InstanceRef objects. This method is shorthand for individually evaluating
  /// each of the getters specified by property names.
  ///
  /// It would be nice if the Observatory protocol provided a built in method
  /// to get InstanceRef objects for a list of properties but this is
  /// sufficient although slightly less efficient. The Observatory protocol
  /// does provide fast access to all fields as part of an Instance object
  /// but that is inadequate as for many Flutter data objects that we want
  /// to display visually we care about properties that are not necessarily
  /// fields.
  ///
  /// The future will immediately complete to null if the inspectorInstanceRef is null.
  Future<Map<String, InstanceRef>> getDartObjectProperties(
    InspectorInstanceRef inspectorInstanceRef,
    final List<String> propertyNames,
  ) async {
    final instanceRef = await toObservatoryInstanceRef(inspectorInstanceRef);
    if (disposed) return null;
    const objectName = 'that';
    final expression =
        '[${propertyNames.map((propertyName) => '$objectName.$propertyName').join(',')}]';
    final Map<String, String> scope = {objectName: instanceRef.id};
    final instance = await getInstance(
        inspectorLibrary.eval(expression, isAlive: this, scope: scope));
    if (disposed) return null;

    // We now have an instance object that is a Dart array of all the
    // property values. Convert it back to a map from property name to
    // property values.

    final Map<String, InstanceRef> properties = {};
    final List<InstanceRef> values = instance.elements.toList();
    assert(values.length == propertyNames.length);
    for (int i = 0; i < propertyNames.length; ++i) {
      properties[propertyNames[i]] = values[i];
    }
    return properties;
  }

  Future<InstanceRef> toObservatoryInstanceRef(
      InspectorInstanceRef inspectorInstanceRef) {
    return invokeServiceMethodObservatoryInspectorRef(
        'toObject', inspectorInstanceRef);
  }

  Future<Instance> getInstance(FutureOr<InstanceRef> instanceRef) async {
    if (disposed) {
      return null;
    }
    return inspectorLibrary.getInstance(await instanceRef, this);
  }

  Future<RemoteDiagnosticsNode> parseDiagnosticsNodeDaemon(
      Future<Object> json) async {
    if (disposed) return null;
    return parseDiagnosticsNodeHelper(await json);
  }

  RemoteDiagnosticsNode parseDiagnosticsNodeHelper(
      Map<String, Object> jsonElement) {
    if (disposed) return null;
    if (jsonElement == null) return null;
    return RemoteDiagnosticsNode(jsonElement, this, false, null);
  }

  /// Requires that the InstanceRef is really referring to a String that is valid JSON.
  Future<Object> instanceRefToJson(InstanceRef instanceRef) async {
    if (disposed || instanceRef == null) return null;
    final instance = await inspectorLibrary.getInstance(instanceRef, this);

    if (disposed || instance == null) return null;

    final String json = instance.valueAsString;
    if (json == null) return null;
    return jsonDecode(json);
  }

  Future<List<RemoteDiagnosticsNode>> parseDiagnosticsNodesObservatory(
      FutureOr<InstanceRef> instanceRefFuture,
      RemoteDiagnosticsNode parent) async {
    if (disposed || instanceRefFuture == null) return [];
    final instanceRef = await instanceRefFuture;
    if (disposed || instanceRefFuture == null) return [];
    return parseDiagnosticsNodesHelper(
        await instanceRefToJson(instanceRef), parent);
  }

  List<RemoteDiagnosticsNode> parseDiagnosticsNodesHelper(
      List<Object> jsonObject, RemoteDiagnosticsNode parent) {
    if (disposed || jsonObject == null) return const [];
    final List<RemoteDiagnosticsNode> nodes = [];
    for (Map<String, Object> element in jsonObject) {
      nodes.add(RemoteDiagnosticsNode(element, this, false, parent));
    }
    return nodes;
  }

  Future<List<RemoteDiagnosticsNode>> parseDiagnosticsNodesDaemon(
      FutureOr<Object> jsonFuture, RemoteDiagnosticsNode parent) async {
    if (disposed || jsonFuture == null) return const [];

    return parseDiagnosticsNodesHelper(await jsonFuture, parent);
  }

  Future<List<RemoteDiagnosticsNode>> getChildren(
    InspectorInstanceRef instanceRef,
    bool summaryTree,
    RemoteDiagnosticsNode parent,
  ) {
    return getListHelper(
      instanceRef,
      summaryTree ? 'getChildrenSummaryTree' : 'getChildrenDetailsSubtree',
      parent,
    );
  }

  Future<List<RemoteDiagnosticsNode>> getProperties(
      InspectorInstanceRef instanceRef) {
    return getListHelper(instanceRef, 'getProperties', null);
  }

  Future<List<RemoteDiagnosticsNode>> getListHelper(
    InspectorInstanceRef instanceRef,
    String methodName,
    RemoteDiagnosticsNode parent,
  ) async {
    if (disposed) return const [];
    if (useDaemonApi) {
      return parseDiagnosticsNodesDaemon(
          invokeServiceMethodDaemonInspectorRef(methodName, instanceRef),
          parent);
    } else {
      return parseDiagnosticsNodesObservatory(
          invokeServiceMethodObservatoryInspectorRef(methodName, instanceRef),
          parent);
    }
  }

  Future<RemoteDiagnosticsNode> invokeServiceMethodReturningNode(
      String methodName) async {
    if (disposed) return null;
    if (useDaemonApi) {
      return parseDiagnosticsNodeDaemon(invokeServiceMethodDaemon(methodName));
    } else {
      return parseDiagnosticsNodeObservatory(
          invokeServiceMethodObservatory(methodName));
    }
  }

  Future<RemoteDiagnosticsNode> invokeServiceMethodReturningNodeInspectorRef(
      String methodName, InspectorInstanceRef ref) {
    if (disposed) return null;
    if (useDaemonApi) {
      return parseDiagnosticsNodeDaemon(
          invokeServiceMethodDaemonInspectorRef(methodName, ref));
    } else {
      return parseDiagnosticsNodeObservatory(
          invokeServiceMethodObservatoryInspectorRef(methodName, ref));
    }
  }

  Future<void> invokeVoidServiceMethod(String methodName, String arg1) async {
    if (disposed) return;
    if (useDaemonApi) {
      await invokeServiceMethodDaemon(methodName, arg1);
    } else {
      await invokeServiceMethodObservatory1(methodName, arg1);
    }
  }

  Future<void> invokeVoidServiceMethodInspectorRef(
      String methodName, InspectorInstanceRef ref) async {
    if (disposed) return;
    if (useDaemonApi) {
      await invokeServiceMethodDaemonInspectorRef(methodName, ref);
    } else {
      await invokeServiceMethodObservatoryInspectorRef(methodName, ref);
    }
  }

  Future<RemoteDiagnosticsNode> getRootWidget() {
    return invokeServiceMethodReturningNode('getRootWidgetSummaryTree');
  }

  Future<RemoteDiagnosticsNode> getRootWidgetFullTree() {
    return invokeServiceMethodReturningNode('getRootWidget');
  }

  Future<RemoteDiagnosticsNode> getSummaryTreeWithoutIds() {
    return parseDiagnosticsNodeDaemon(
        invokeServiceMethodDaemon('getRootWidgetSummaryTree'));
  }

  Future<RemoteDiagnosticsNode> getRootRenderObject() {
    assert(!disposed);
    return invokeServiceMethodReturningNode('getRootRenderObject');
  }

  //// TODO(jacobr): this probably isn't needed.
  ////Future<List<DiagnosticsPathNode>> getParentChain(DiagnosticsNode target) async {
  ////if (disposed) return null;
  ////if (useDaemonApi) {
  ////return parseDiagnosticsPathDaemon(invokeServiceMethodDaemon('getParentChain', target.getValueRef()));
  ////}
  ////else {
  ////return parseDiagnosticsPathObservatory(invokeServiceMethodObservatory('getParentChain', target.getValueRef()));
  ////}
  ////});
  ////}

  ////Future<List<DiagnosticsPathNode>> parseDiagnosticsPathObservatory(Future<InstanceRef> instanceRefFuture) {
  ////return nullIfDisposed(() -> instanceRefFuture.thenComposeAsync(this::parseDiagnosticsPathObservatory));
  ////}

  ////Future<List<DiagnosticsPathNode>> parseDiagnosticsPathObservatory(InstanceRef pathRef) {
  ////return nullIfDisposed(() -> instanceRefToJson(pathRef).thenApplyAsync(this::parseDiagnosticsPathHelper));
  ////}

  ////Future<List<DiagnosticsPathNode>> parseDiagnosticsPathDaemon(Future<JsonElement> jsonFuture) {
  ////return nullIfDisposed(() -> jsonFuture.thenApplyAsync(this::parseDiagnosticsPathHelper));
  ////}

  ////List<DiagnosticsPathNode> parseDiagnosticsPathHelper(JsonElement jsonElement) {
  ////return nullValueIfDisposed(() -> {
  ////final JsonArray jsonArray = jsonElement.getAsJsonArray();
  ////final List<DiagnosticsPathNode> pathNodes = new List<>();
  ////for (JsonElement element : jsonArray) {
  ////pathNodes.add(new DiagnosticsPathNode(element.getAsJsonObject(), this));
  ////}
  ////return pathNodes;
  ////});
  ////}


  Future<RemoteDiagnosticsNode> getSelection(
    RemoteDiagnosticsNode previousSelection,
    FlutterTreeType treeType, {
    @required bool isSummaryTree,
  }) async {
    assert(isSummaryTree != null);
    // There is no reason to allow calling this method on a disposed group.
    assert(!disposed);
    if (disposed) return null;
    RemoteDiagnosticsNode newSelection;
    final InspectorInstanceRef previousSelectionRef =
        previousSelection != null ? previousSelection.dartDiagnosticRef : null;

    switch (treeType) {
      case FlutterTreeType.widget:
        newSelection = await invokeServiceMethodReturningNodeInspectorRef(
            isSummaryTree ? 'getSelectedSummaryWidget' : 'getSelectedWidget',
            previousSelectionRef);
        break;
      case FlutterTreeType.renderObject:
        newSelection = await invokeServiceMethodReturningNodeInspectorRef(
            'getSelectedRenderObject', previousSelectionRef);
        break;
    }
    if (disposed) return null;

    if (newSelection != null &&
        newSelection.dartDiagnosticRef == previousSelectionRef) {
      return previousSelection;
    } else {
      return newSelection;
    }
  }

  Future<void> setSelectionInspector(
      InspectorInstanceRef selection, bool uiAlreadyUpdated) {
    if (disposed) {
      return Future.value();
    }
    inspectorService._trackClientSelfTriggeredSelection(selection);
    if (useDaemonApi) {
      return handleSetSelectionDaemon(
          invokeServiceMethodDaemonInspectorRef('setSelectionById', selection),
          uiAlreadyUpdated);
    } else {
      return handleSetSelectionObservatory(
          invokeServiceMethodObservatoryInspectorRef(
              'setSelectionById', selection),
          uiAlreadyUpdated);
    }
  }

  Future<void> handleSetSelectionObservatory(
      Future<InstanceRef> setSelectionResult, bool uiAlreadyUpdated) async {
    // TODO(jacobr): we need to cancel if another inspect request comes in while we are trying this one.
    if (disposed) return;
    final instanceRef = await setSelectionResult;
    if (disposed) return;
    handleSetSelectionHelper(
        'true' == instanceRef?.valueAsString, uiAlreadyUpdated);
  }

  void handleSetSelectionHelper(bool selectionChanged, bool uiAlreadyUpdated) {
    if (selectionChanged && !uiAlreadyUpdated && !disposed) {
      inspectorService.notifySelectionChanged();
    }
  }

  Future<void> handleSetSelectionDaemon(
      Future<Object> setSelectionResult, bool uiAlreadyUpdated) async {
    if (disposed) return;
    // TODO(jacobr): we need to cancel if another inspect request comes in while we are trying this one.
    final json = await setSelectionResult;
    if (disposed) return;
    handleSetSelectionHelper(json, uiAlreadyUpdated);
  }

  Future<Map<String, InstanceRef>> getEnumPropertyValues(
      InspectorInstanceRef ref) async {
    if (disposed) return null;
    if (ref?.id == null) return null;

    final instance = await getInstance(await toObservatoryInstanceRef(ref));
    if (disposed || instance == null) return null;

    final clazz = await inspectorLibrary.getClass(instance.classRef, this);
    if (disposed || clazz == null) return null;

    final Map<String, InstanceRef> properties = {};
    for (FieldRef field in clazz.fields) {
      final String name = field.name;
      if (name.startsWith('_')) {
        // Needed to filter out _deleted_enum_sentinel synthetic property.
        // If showing enum values is useful we could special case
        // just the _deleted_enum_sentinel property name.
        continue;
      }
      if (name == 'values') {
        // Need to filter out the synthetic 'values' member.
        // TODO(jacobr): detect that this properties return type is
        // different and filter that way.
        continue;
      }
      if (field.isConst && field.isStatic) {
        properties[field.name] = field.declaredType;
      }
    }
    return properties;
  }

  Future<RemoteDiagnosticsNode> getDetailsSubtree(
    RemoteDiagnosticsNode node, {
    int subtreeDepth = 2,
  }) async {
    if (node == null) return null;
    final args = {
      'objectGroup': groupName,
      'arg': node.dartDiagnosticRef.id,
      'subtreeDepth': subtreeDepth.toString(),
    };
    final json = await invokeServiceMethodDaemonParams(
      'getDetailsSubtree',
      args,
    );
    return parseDiagnosticsNodeHelper(json);
  }

  Future<void> invokeSetFlexProperties(
    InspectorInstanceRef ref,
    MainAxisAlignment mainAxisAlignment,
    CrossAxisAlignment crossAxisAlignment,
  ) async {
    if (ref == null) return null;
    await invokeServiceExtensionMethod(
      RegistrableServiceExtension.setFlexProperties,
      {
        'id': ref.id,
        'mainAxisAlignment': '$mainAxisAlignment',
        'crossAxisAlignment': '$crossAxisAlignment',
      },
    );
  }

  Future<void> invokeSetFlexFactor(
    InspectorInstanceRef ref,
    int flexFactor,
  ) async {
    if (ref == null) return null;
    await invokeServiceExtensionMethod(
      RegistrableServiceExtension.setFlexFactor,
      {'id': ref.id, 'flexFactor': '$flexFactor'},
    );
  }

  Future<void> invokeSetFlexFit(
    InspectorInstanceRef ref,
    FlexFit flexFit,
  ) async {
    if (ref == null) return null;
    await invokeServiceExtensionMethod(
      RegistrableServiceExtension.setFlexFit,
      {'id': ref.id, 'flexFit': '$flexFit'},
    );
  }

  Future<RemoteDiagnosticsNode> getLayoutExplorerNode(
    RemoteDiagnosticsNode node, {
    int subtreeDepth = 1,
  }) async {
    if (node == null) return null;
    return parseDiagnosticsNodeDaemon(invokeServiceExtensionMethod(
      RegistrableServiceExtension.getLayoutExplorerNode,
      {
        'groupName': groupName,
        'id': node.dartDiagnosticRef.id,
        'subtreeDepth': '$subtreeDepth',
      },
    ));
  }

  Future<List<String>> getPubRootDirectories() async {
    final List<Object> directories = await invokeServiceExtensionMethod(
      RegistrableServiceExtension.getPubRootDirectories,
      {},
    );
    return List.from(directories ?? []);
  }
}

public enum FlutterTreeType {
  widget, // ('Widget'),
  renderObject // ('Render');
// TODO(jacobr): add semantics, and layer trees.
}

public abstract class InspectorServiceClient {
  void onInspectorSelectionChanged();

  void onFlutterFrame();

  Future<Object> onForceRefresh();
}

/// Reference to a Dart object.
///
/// This class is similar to the Observatory protocol InstanceRef with the
/// difference that InspectorInstanceRef objects do not expire and all
/// instances of the same Dart object are guaranteed to have the same
/// InspectorInstanceRef id. The tradeoff is the consumer of
/// InspectorInstanceRef objects is responsible for managing their lifecycles.
public class InspectorInstanceRef {
  public InspectorInstanceRef(string id)
  {
    this.id = id;
  }

  
  public override bool operator ==(object other) {
    if (other is InspectorInstanceRef) {
      return id == ((InspectorInstanceRef)other).id;
    }
    return false;
  }

  
  public override int hashCode => id.hashCode;

  
  public override string ToString() => id;

  public readonly string id;
}

/// Manager that simplifies preventing memory leaks when using the
/// InspectorService.
///
/// This class is designed for the use case where you want to manage
/// object references associated with the current displayed UI and object
/// references associated with the candidate next frame of UI to display. Once
/// the next frame is ready, you determine whether you want to display it and
/// discard the current frame and promote the next frame to the the current
/// frame if you want to display the next frame otherwise you discard the next
/// frame.
///
/// To use this class load all data you want for the next frame by using
/// the object group specified by [next] and then if you decide to switch
/// to display that frame, call promoteNext() otherwise call clearNext().
public class InspectorObjectGroupManager {
  public InspectorObjectGroupManager(InspectorService inspectorService, string debugName)
  {
    this.inspectorService = inspectorService;
    this.debugName = debugName;
  }

  public readonly InspectorService inspectorService;
  public readonly string debugName;
  ObjectGroup _current;
  ObjectGroup _next;

  Completer _pendingNext;

  Future pendingUpdateDone {
    get
    {
      if (_pendingNext != null) {
        return _pendingNext.future;
      }
      if (_next == null) {
        // There is no pending update.
        return Future.value();
    }
      _pendingNext = new Completer();
      return _pendingNext.future;
    }

  }

  ObjectGroup current {
    get
    {
      _current = _current ??inspectorService.createObjectGroup(debugName);
      return _current;
    }
    
  }

  ObjectGroup next {
    get
    {
      _next = _next ??inspectorService.createObjectGroup(debugName);
      return _next;
    }
    
  }

  public void clear(bool isolateStopped) {
    if (isolateStopped) {
      // The Dart VM will handle GCing the underlying memory.
      _current = null;
      _setNextNull();
    } else {
      clearCurrent();
      cancelNext();
    }
  }

  void promoteNext() {
    clearCurrent();
    _current = _next;
    _setNextNull();
  }

  void clearCurrent() {
    if (_current != null) {
      _current.dispose();
      _current = null;
    }
  }

  void cancelNext() {
    if (_next != null) {
      _next.dispose();
      _setNextNull();
    }
  }

  void _setNextNull() {
    _next = null;
    if (_pendingNext != null) {
      _pendingNext.complete(null);
      _pendingNext = null;
    }
  }
}

// public class FlutterInspectorLibraryNotFound : LibraryNotFound {
//   public FlutterInspectorLibraryNotFound() : base(inspectorLibraryUriCandidates)
//   {
//     
//   }
// }

}*/
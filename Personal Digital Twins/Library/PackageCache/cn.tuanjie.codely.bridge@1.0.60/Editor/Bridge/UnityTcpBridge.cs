using System;
using System.Collections;
using System.Collections.Generic;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;
using UnityTcp.Editor.Native;
using UnityTcp.Editor.Tools;

namespace UnityTcp.Editor
{
    /// <summary>
    /// Command structure for JSON deserialization
    /// </summary>
    public class Command
    {
        public string type { get; set; }
        public JObject @params { get; set; }
    }

    public class Notification
    {
        public string notification_type { get; set; }
        public JObject payload { get; set; }
    }

    [InitializeOnLoad]
    public static partial class UnityTcpBridge
    {
        private static bool isRunning = false;
        private const string ManualStopPrefKey = "UnityTcp.ManualStop";
        public static event Action OnClientPlatformsChanged;
        private static double nextHeartbeatAt = 0.0;
        private static string serverVer = "1.0.0-beta.1";
        private static int heartbeatSeq = 0;
        private static int currentUnityPort = -1;
        private const ulong MaxFrameBytes = 64UL * 1024 * 1024; // 64 MiB hard cap for framed payloads
        private const int FrameIOTimeoutMs = 3000; // Per-read timeout to avoid stalled clients
        private const int NativePollLimitPerTick = 128;
        private static int previousConnectionCount = -1;

        // --- Coroutine command system ---

        private class AsyncCommandEntry
        {
            public ulong RequestId;
            public Command Command;
        }

        private readonly struct RunningCoroutine
        {
            public readonly ulong RequestId;
            public readonly IEnumerator Enumerator;
            public RunningCoroutine(ulong requestId, IEnumerator enumerator)
            {
                RequestId  = requestId;
                Enumerator = enumerator;
            }
        }

        private static readonly Queue<AsyncCommandEntry> _asyncCommandQueue = new Queue<AsyncCommandEntry>();
        private static readonly List<RunningCoroutine>   _runningCoroutines  = new List<RunningCoroutine>();

        // Debug helpers
        private unsafe static bool IsDebugEnabled()
        {
            try { return EditorPrefs.GetBool("UnityTcp.DebugLogs", false); } catch { return false; }
        }

        private static void LogBreadcrumb(string stage)
        {
            if (IsDebugEnabled())
            {
                CodelyLogger.Verbose($"[{stage}]");
            }
        }

        public static bool IsRunning => isRunning;
        public static int GetCurrentPort() => currentUnityPort;

        /// <summary>
        /// Get all connected clients with their platform types
        /// </summary>
        public static Dictionary<string, string> GetConnectedClients()
        {
            if (!isRunning)
            {
                return new Dictionary<string, string>();
            }

            return NativeUnityTcpBridgeHost.GetConnectedClients();
        }



        static UnityTcpBridge()
        {
            // Initialize main thread ID for safe thread checks
            MainThreadHelper.InitializeMainThreadId();

            // Register the FindObjectByInstruction delegate for UnityEngineObjectConverter
            UnityTcp.Editor.Serialization.UnityEngineObjectConverter.FindObjectByInstruction = ManageGameObject.FindObjectByInstruction;

            // CI override: set UNITY_TCP_ALLOW_BATCH=1 to allow the bridge in batch mode
            if (Application.isBatchMode && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_TCP_ALLOW_BATCH")))
            {
                return;
            }

            // Respect manual stop across domain reloads

            if (!EditorPrefs.GetBool(ManualStopPrefKey, false))
            {
                Start();
            }

        }

        private static void OnNativeDllUnload() => Stop();

        private static void OnEditorQuitting()
        {
            Stop();
        }

        public static void Start()
        {
            // Clear any prior manual-stop so the bridge can run (and survive future domain reloads).
            EditorPrefs.DeleteKey(ManualStopPrefKey);

            // isRunning is guarded first to short-circuit the IsRunning() call (requires DLL loaded).
            if (isRunning && NativeUnityTcpBridgeHost.IsRunning())
            {
                if (IsDebugEnabled())
                    CodelyLogger.Log($"<b><color=#2EA3FF>Codely-Bridge</color></b>: UnityTcpBridge already running on port {currentUnityPort}");
                return;
            }

            try
            {
                // Unified native bridge: one TCP listener serves both commands and notifications.
                // StartOrAttach loads the DLL if needed and handles the already-running case natively.
                if (!NativeUnityTcpBridgeHost.StartOrAttach(0, (int)MaxFrameBytes, out int boundPort))
                    throw new Exception("Native Codely Bridge failed to start.");

                currentUnityPort = boundPort;
                isRunning = true;
                previousConnectionCount = NativeUnityTcpBridgeHost.GetConnectionCount();

                CodelyLogger.Log($"<b><color=#2EA3FF>Codely-Bridge</color></b>: Native Codely Bridge running on port {currentUnityPort}. (OS={Application.platform}, server={serverVer})");

                EditorApplication.update -= ProcessCommands;
                EditorApplication.update += ProcessCommands;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload  -= OnAfterAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload  += OnAfterAssemblyReload;
                EditorApplication.quitting -= OnEditorQuitting;
                EditorApplication.quitting += OnEditorQuitting;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                Native.NativeDllLoader.OnUnload -= OnNativeDllUnload;
                Native.NativeDllLoader.OnUnload += OnNativeDllUnload;
                heartbeatSeq++;
                StatusHelper.WriteHeartbeat(currentUnityPort, false, heartbeatSeq, "ready");
                nextHeartbeatAt = EditorApplication.timeSinceStartup + 0.5f;
            }
            catch (Exception ex)
            {
                isRunning = false;
                CodelyLogger.LogError($"<b><color=#2EA3FF>Codely-Bridge</color></b>: Native-only mode failed to start. {ex.Message}");
            }
        }

        public static void Stop(bool isManualStop = false)
        {
            if (isManualStop)
                EditorPrefs.SetBool(ManualStopPrefKey, true);
            if (!isRunning) return;

            isRunning = false;

            EditorApplication.update -= ProcessCommands;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload  -= OnAfterAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Native.NativeDllLoader.OnUnload -= OnNativeDllUnload;

            // Drain before stopping: EnqueueResponse calls into native memory that StopServer frees.
            DrainPendingCommandsWithError("Bridge stopped while command was queued.");
            NativeUnityTcpBridgeHost.StopServer();

            currentUnityPort = -1;

            if (IsDebugEnabled()) CodelyLogger.Log("<b><color=#2EA3FF>Codely-Bridge</color></b>: UnityTcpBridge stopped.");

            StatusHelper.WriteHeartbeat(-1, false, heartbeatSeq, "stopped");
        }

        private static void DrainPendingCommandsWithError(string reason)
        {
            string errorJson = JsonConvert.SerializeObject(Response.Error(reason));

            while (_asyncCommandQueue.Count > 0)
                NativeUnityTcpBridgeHost.EnqueueResponse(_asyncCommandQueue.Dequeue().RequestId, errorJson);

            foreach (var c in _runningCoroutines)
                NativeUnityTcpBridgeHost.EnqueueResponse(c.RequestId, errorJson);

            _runningCoroutines.Clear();
        }

        private static void ProcessCommands()
        {
            if (!isRunning) return;

            if (!NativeUnityTcpBridgeHost.IsRunning())
            {
                isRunning = false;
                return;
            }

            int polled = 0;
            while (polled < NativePollLimitPerTick
                   && NativeUnityTcpBridgeHost.TryDequeueCommand(out ulong requestId, out string commandText))
            {
                polled++;
                ProcessSingleCommand(requestId, commandText);
            }

            double now = EditorApplication.timeSinceStartup;
            if (now >= nextHeartbeatAt)
            {
                StatusHelper.WriteHeartbeat(currentUnityPort, false, heartbeatSeq);
                nextHeartbeatAt = now + 0.5f;
            }

            NotifyClientPlatformChangesIfNeeded();
            PumpCoroutineQueue();
        }

        private static void ProcessSingleCommand(ulong requestId, string commandText)
        {
            commandText = commandText?.Trim();

            if (string.IsNullOrEmpty(commandText))
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Empty command received")));
                return;
            }

            if (!JsonCommandHelper.IsValidJson(commandText))
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Invalid JSON format", new
                {
                    receivedText = commandText.Length > 50 ? commandText.Substring(0, 50) + "..." : commandText
                })));
                return;
            }

            Command command = JsonConvert.DeserializeObject<Command>(commandText);
            if (command == null)
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Command deserialized to null")));
                return;
            }

            if (IsCoroutineCommand(command.type))
            {
                _asyncCommandQueue.Enqueue(new AsyncCommandEntry { RequestId = requestId, Command = command });
                return;
            }

            try
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, ExecuteCommand(command));
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error(ex.Message, new
                {
                    commandType = command.type,
                    stackTrace = ex.StackTrace
                })));
            }
        }

        private static void NotifyClientPlatformChangesIfNeeded()
        {
            int current = NativeUnityTcpBridgeHost.GetConnectionCount();
            if (current != previousConnectionCount)
            {
                previousConnectionCount = current;
                try
                {
                    OnClientPlatformsChanged?.Invoke();
                }
                catch
                {
                    // Keep polling path resilient even if UI callbacks fail.
                }
            }
        }



        private static string ExecuteCommand(Command command)
        {
            try
            {
                if (string.IsNullOrEmpty(command.type))
                {
                    var errorResponse = Response.Error("Command type cannot be empty",
                        "A valid command type is required for processing");
                    return JsonConvert.SerializeObject(errorResponse);
                }

                // Use JObject for parameters as the new handlers likely expect this
                JObject paramsObject = command.@params ?? new JObject();

                // Route command based on the tool structure from the existing project
                object result;
                switch (command.type)
                {
                    case "manage_script":
                        result = ManageScript.HandleCommand(paramsObject);
                        break;
                    case "manage_workflow":
                        result = ManageWorkflow.HandleCommand(paramsObject);
                        break;
                    case "manage_scene":
                        result = HandleManageScene(paramsObject)
                            ?? throw new TimeoutException($"manage_scene timed out after {FrameIOTimeoutMs} ms on main thread");
                        break;
                    case "manage_editor":
                        result = ManageEditor.HandleCommand(paramsObject);
                        break;
                    case "manage_gameobject":
                        result = ManageGameObject.HandleCommand(paramsObject);
                        break;
                    case "manage_asset":
                        result = ManageAsset.HandleCommand(paramsObject);
                        break;
                    case "manage_shader":
                        result = ManageShader.HandleCommand(paramsObject);
                        break;
                    case "read_console":
                        result = ReadConsole.HandleCommand(paramsObject);
                        break;
                    case "execute_menu_item":
                        result = ExecuteMenuItem.HandleCommand(paramsObject);
                        break;
                    case "execute_csharp_script":
                        result = ExecuteCSharpScript.HandleCommand(paramsObject);
                        break;
                    case "manage_package":
                        result = ManagePackage.HandleCommand(paramsObject);
                        break;
                    case "manage_bake":
                        result = ManageBake.HandleCommand(paramsObject);
                        break;
                    case "manage_ui_toolkit":
                        result = ManageUIToolkit.HandleCommand(paramsObject);
                        break;
                    case "execute_custom_tool":
                        result = ExecuteCustomTool.HandleCommand(paramsObject);
                        break;
                    case "_internal_state_dirty":
                        result = _InternalStateDirtyNotifier.HandleCommand(paramsObject);
                        break;
                    case "manage_window_bridge":
                        result = ManageWindowBridge.HandleCommand(paramsObject);
                        break;
                    case "manage_webrtc_stream":
                        result = ManageWebRtcStream.HandleCommand(paramsObject);
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown or unsupported command type: {command.type}"
                        );
                }

                result = StateDirtyPolicy.Apply(
                    command.type,
                    paramsObject["action"]?.ToString(),
                    result
                );

                // Convert result to success response format compatible with Response helper
                return JsonConvert.SerializeObject(Response.Success("Command executed successfully", result));
            }
            catch (Exception ex)
            {
                // Log the detailed error in Unity for debugging
                CodelyLogger.LogError(
                    $"Error executing command '{command?.type ?? "Unknown"}': {ex.Message}\n{ex.StackTrace}"
                );

                // Use Response helper for consistent error format
                var response = Response.Error(ex.Message, new
                {
                    command = command?.type ?? "Unknown",
                    stackTrace = ex.StackTrace,
                    paramsSummary = command?.@params != null
                        ? JsonCommandHelper.GetParamsSummary(command.@params)
                        : "No parameters"
                });
                return JsonConvert.SerializeObject(response);
            }
        }

        private static object HandleManageScene(JObject paramsObject)
        {
            try
            {
                if (IsDebugEnabled()) CodelyLogger.Log("[TCP] manage_scene: dispatching to main thread");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = MainThreadHelper.InvokeOnMainThreadWithTimeout(() => ManageScene.HandleCommand(paramsObject), FrameIOTimeoutMs);
                sw.Stop();
                if (IsDebugEnabled()) CodelyLogger.Log($"[TCP] manage_scene: completed in {sw.ElapsedMilliseconds} ms");
                return r ?? Response.Error("manage_scene returned null (timeout or error)");
            }
            catch (Exception ex)
            {
                return Response.Error($"manage_scene dispatch error: {ex.Message}");
            }
        }


        // --- Coroutine command system implementation ---

        private static bool IsCoroutineCommand(string type)
        {
            switch (type)
            {
                case "manage_screenshot": return true;
                default:                  return false;
            }
        }

        /// <summary>
        /// Called every editor tick from ProcessCommands.
        /// Starts all queued async commands as new coroutines, then advances every running coroutine by one step.
        /// </summary>
        private static void PumpCoroutineQueue()
        {
            while (_asyncCommandQueue.Count > 0)
            {
                var entry = _asyncCommandQueue.Dequeue();
                try
                {
                    _runningCoroutines.Add(new RunningCoroutine(
                        entry.RequestId,
                        ExecuteAsyncCommandCoroutine(entry.RequestId, entry.Command)));
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"[Coroutine] Failed to create coroutine for '{entry.Command?.type}': {ex.Message}");
                    NativeUnityTcpBridgeHost.EnqueueResponse(entry.RequestId, JsonConvert.SerializeObject(Response.Error(ex.Message)));
                }
            }

            for (int i = _runningCoroutines.Count - 1; i >= 0; i--)
            {
                bool finished;
                try { finished = !_runningCoroutines[i].Enumerator.MoveNext(); }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"[Coroutine] Unhandled exception: {ex.Message}\n{ex.StackTrace}");
                    finished = true;
                }
                if (finished) _runningCoroutines.RemoveAt(i);
            }
        }

        private static IEnumerator ExecuteAsyncCommandCoroutine(ulong requestId, Command command)
        {
            string result = null;
            Exception caughtEx = null;

            JObject p = command.@params ?? new JObject();
            IEnumerator inner;
            switch (command.type)
            {
                case "manage_screenshot":
                    inner = ManageScreenshot.HandleCommandCoroutine(p, r => result = r);
                    break;
                default:
                    throw new ArgumentException($"No coroutine handler for: {command.type}");
            }

            while (true)
            {
                bool hasNext;
                try { hasNext = inner.MoveNext(); }
                catch (Exception ex) { caughtEx = ex; break; }
                if (!hasNext) break;
                yield return inner.Current;
            }

            string responseJson = caughtEx != null
                ? JsonConvert.SerializeObject(Response.Error(caughtEx.Message, new { command = command?.type, stackTrace = caughtEx.StackTrace }))
                : result ?? JsonConvert.SerializeObject(Response.Success("No result produced."));

            NativeUnityTcpBridgeHost.EnqueueResponse(requestId, responseJson);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Drain coroutines and async queue before Unity tears down play-mode objects.
                // Without this, PumpCoroutineQueue advances coroutines against destroyed objects.
                DrainPendingCommandsWithError("Play mode exiting.");
            }
        }

        // Heartbeat/status helpers
        private static void OnBeforeAssemblyReload()
        {
            NativeUnityTcpBridgeHost.SetIsCSharpAssemblyReloading(true);
            DrainPendingCommandsWithError("C# is performing domain reloading.");
        }

        private static void OnAfterAssemblyReload()
        {
            NativeUnityTcpBridgeHost.SetIsCSharpAssemblyReloading(false);
        }

        // ---- Notify API ---------------------------------------------------- //

        /// <summary>
        /// Broadcasts a push notification to every client that advertised CLIENT_VERSION>=2.
        /// In the unified bridge, notifications share the same socket as commands.
        /// </summary>
        /// <param name="eventType">Event type identifier (e.g. "scene_changed").</param>
        /// <param name="payload">Optional payload object; serialized to JSON.</param>
        /// <returns>True if the notification was enqueued to at least one client.</returns>
        public static bool NotifyAll(string eventType, JObject payload = null)
        {
            if (!isRunning || string.IsNullOrWhiteSpace(eventType))
                return false;

            string json = JsonConvert.SerializeObject(new Notification { notification_type = eventType, payload = payload });
            return NativeUnityTcpBridgeHost.NotifyAll(json);
        }
    }
}

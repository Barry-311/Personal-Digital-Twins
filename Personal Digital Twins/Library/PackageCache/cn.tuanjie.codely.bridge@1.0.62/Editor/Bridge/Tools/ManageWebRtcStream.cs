using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Manages Editor-side WebRTC streaming using Unity RenderStreaming via reflection.
    /// This implementation intentionally avoids hard assembly references so the bridge can
    /// still compile when RenderStreaming package is not installed.
    /// </summary>
    public static class ManageWebRtcStream
    {
        private static readonly object SessionLock = new object();
        private static readonly RenderStreamingSession Session = new RenderStreamingSession();

        static ManageWebRtcStream()
        {
            EditorApplication.quitting += StopSessionSafe;
            AssemblyReloadEvents.beforeAssemblyReload += StopSessionSafe;
        }

        public static object HandleCommand(JObject @params)
        {
            string action = (@params?["action"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return Response.Error("Action is required for manage_webrtc_stream.");
            }

            switch (action)
            {
                case "start_session": return StartSession(@params);
                case "stop_session": return StopSession();
                case "get_status": return GetStatus();
                case "list_sources": return ListSources();
                case "inject_input": return InjectInput(@params);
                default:
                    return Response.Error(
                        $"Unknown action '{action}'.",
                        new { validActions = new[] { "start_session", "stop_session", "get_status", "list_sources", "inject_input" } }
                    );
            }
        }

        /// <summary>
        /// IPC entry used by CodelyIpcServer without direct assembly dependency.
        /// </summary>
        public static string StartSessionFromIpc(string jsonPayload)
        {
            try
            {
                JObject payload = string.IsNullOrWhiteSpace(jsonPayload)
                    ? new JObject()
                    : JsonConvert.DeserializeObject<JObject>(jsonPayload) ?? new JObject();
                object result = StartSession(payload);
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(Response.Error("IPC start session failed.", new { ex.Message }));
            }
        }

        /// <summary>
        /// IPC entry used by CodelyIpcServer without direct assembly dependency.
        /// </summary>
        public static string StopSessionFromIpc()
        {
            return JsonConvert.SerializeObject(StopSession());
        }

        /// <summary>
        /// IPC entry used by CodelyIpcServer without direct assembly dependency.
        /// </summary>
        public static bool InjectInputFromIpc(string jsonPayload)
        {
            return TryInjectInputEvent(jsonPayload, out _);
        }

        private static object ListSources()
        {
            return Response.Success(
                "Supported sources listed.",
                new
                {
                    sources = new[]
                    {
                        new { id = "game_view", description = "Capture Unity GameView render texture." },
                        new { id = "scene_view", description = "Capture SceneView camera render." },
                        new { id = "editor_window", description = "Capture a target EditorWindow texture via reflection (best effort)." }
                    }
                }
            );
        }

        private static object GetStatus()
        {
            lock (SessionLock)
            {
                return Response.Success("WebRTC session status.", Session.BuildStatusData());
            }
        }

        private static object StartSession(JObject @params)
        {
            if (!RenderStreamingSession.IsRenderStreamingAvailable(out string availabilityReason))
            {
                return Response.Error(
                    "RenderStreaming package is unavailable.",
                    new
                    {
                        reason = availabilityReason,
                        suggestion = "Install com.unity.renderstreaming and com.unity.webrtc packages."
                    }
                );
            }

            string source = (@params["source"]?.ToString() ?? "game_view").Trim().ToLowerInvariant();
            string editorWindowType = (@params["editor_window_type"]?.ToString() ?? string.Empty).Trim();
            int width = ClampOrDefault(@params["width"], 1280, 320, 3840);
            int height = ClampOrDefault(@params["height"], 720, 180, 2160);
            int fps = ClampOrDefault(@params["fps"], 20, 5, 60);
            int minBitrateKbps = ClampOrDefault(@params["min_bitrate_kbps"], 300, 0, 100000);
            int maxBitrateKbps = ClampOrDefault(@params["max_bitrate_kbps"], 4000, 1, 100000);
            if (minBitrateKbps > maxBitrateKbps)
            {
                (minBitrateKbps, maxBitrateKbps) = (maxBitrateKbps, minBitrateKbps);
            }

            string signalingUrl =
                @params["signaling_url"]?.ToString()
                ?? BuildSignalingUrlFromTauri();
            if (string.IsNullOrWhiteSpace(signalingUrl))
            {
                return Response.Error(
                    "No signaling URL provided and failed to infer from Tauri.",
                    new { requiredParam = "signaling_url" }
                );
            }

            if (!Uri.TryCreate(signalingUrl, UriKind.Absolute, out Uri wsUri) ||
                (wsUri.Scheme != "ws" && wsUri.Scheme != "wss"))
            {
                return Response.Error(
                    "Invalid signaling_url.",
                    new { signalingUrl }
                );
            }

            var options = new SessionOptions
            {
                Source = source,
                EditorWindowType = editorWindowType,
                Width = width,
                Height = height,
                Fps = fps,
                MinBitrateKbps = minBitrateKbps,
                MaxBitrateKbps = maxBitrateKbps,
                SignalingUrl = signalingUrl
            };

            lock (SessionLock)
            {
                if (!Session.Start(options, out string error))
                {
                    return Response.Error(
                        "Failed to start WebRTC session.",
                        new { error, options }
                    );
                }

                return Response.Success(
                    "WebRTC session started.",
                    Session.BuildStatusData()
                );
            }
        }

        private static object StopSession()
        {
            lock (SessionLock)
            {
                Session.Stop();
                return Response.Success("WebRTC session stopped.");
            }
        }

        private static object InjectInput(JObject @params)
        {
            string payload = @params?["event"] != null
                ? @params["event"].ToString(Formatting.None)
                : (@params?.ToString(Formatting.None) ?? string.Empty);

            bool ok = TryInjectInputEvent(payload, out string error);
            return ok
                ? Response.Success("Input event injected.")
                : Response.Error("Failed to inject input event.", new { error });
        }

        private static bool TryInjectInputEvent(string jsonPayload, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(jsonPayload))
                {
                    error = "Input payload is empty.";
                    return false;
                }

                var payload = JsonConvert.DeserializeObject<InputEventPayload>(jsonPayload);
                if (payload == null)
                {
                    error = "Input payload cannot be parsed.";
                    return false;
                }

                EditorWindow targetWindow = ResolveTargetWindow(payload);
                if (targetWindow == null)
                {
                    error = "Target editor window not found.";
                    return false;
                }

                Event unityEvent = BuildUnityEvent(payload, targetWindow);
                if (unityEvent == null)
                {
                    error = $"Unsupported input event type: {payload.eventType}";
                    return false;
                }

                targetWindow.Focus();
                targetWindow.SendEvent(unityEvent);
                targetWindow.Repaint();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static EditorWindow ResolveTargetWindow(InputEventPayload payload)
        {
            string source = (payload.source ?? "game_view").Trim().ToLowerInvariant();
            if (source == "scene_view")
            {
                return SceneView.lastActiveSceneView;
            }

            if (source == "editor_window" && !string.IsNullOrWhiteSpace(payload.editorWindowType))
            {
                var t = Type.GetType(payload.editorWindowType);
                if (t != null)
                {
                    var wins = Resources.FindObjectsOfTypeAll(t);
                    return wins.OfType<EditorWindow>().FirstOrDefault();
                }
            }

            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                return null;
            }

            if (EditorWindow.focusedWindow != null && EditorWindow.focusedWindow.GetType() == gameViewType)
            {
                return EditorWindow.focusedWindow;
            }

            return Resources.FindObjectsOfTypeAll(gameViewType).OfType<EditorWindow>().FirstOrDefault();
        }

        private static Event BuildUnityEvent(InputEventPayload payload, EditorWindow targetWindow)
        {
            string eventType = (payload.eventType ?? string.Empty).Trim().ToLowerInvariant();
            EventModifiers modifiers = EventModifiers.None;
            if (payload.alt) modifiers |= EventModifiers.Alt;
            if (payload.ctrl) modifiers |= EventModifiers.Control;
            if (payload.shift) modifiers |= EventModifiers.Shift;
            if (payload.command) modifiers |= EventModifiers.Command;

            Vector2 mousePosition = ResolveMousePosition(payload, targetWindow);

            switch (eventType)
            {
                case "mouse_move":
                    return new Event { type = EventType.MouseMove, mousePosition = mousePosition, modifiers = modifiers };
                case "mouse_down":
                    return new Event { type = EventType.MouseDown, mousePosition = mousePosition, button = payload.button, modifiers = modifiers };
                case "mouse_up":
                    return new Event { type = EventType.MouseUp, mousePosition = mousePosition, button = payload.button, modifiers = modifiers };
                case "mouse_drag":
                    return new Event { type = EventType.MouseDrag, mousePosition = mousePosition, button = payload.button, modifiers = modifiers };
                case "scroll":
                case "wheel":
                    return new Event
                    {
                        type = EventType.ScrollWheel,
                        mousePosition = mousePosition,
                        delta = new Vector2((float)payload.deltaX, (float)payload.deltaY),
                        modifiers = modifiers
                    };
                case "key_down":
                    return new Event
                    {
                        type = EventType.KeyDown,
                        keyCode = ParseKeyCode(payload.key),
                        character = ParseCharacter(payload.key),
                        modifiers = modifiers
                    };
                case "key_up":
                    return new Event
                    {
                        type = EventType.KeyUp,
                        keyCode = ParseKeyCode(payload.key),
                        character = ParseCharacter(payload.key),
                        modifiers = modifiers
                    };
                default:
                    return null;
            }
        }

        private static Vector2 ResolveMousePosition(InputEventPayload payload, EditorWindow targetWindow)
        {
            if (payload.normalizedX >= 0 && payload.normalizedY >= 0)
            {
                float px = Mathf.Clamp01((float)payload.normalizedX) * targetWindow.position.width;
                float py = Mathf.Clamp01((float)payload.normalizedY) * targetWindow.position.height;
                return new Vector2(px, py);
            }

            return new Vector2((float)payload.x, (float)payload.y);
        }

        private static KeyCode ParseKeyCode(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return KeyCode.None;
            }

            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    return (KeyCode)Enum.Parse(typeof(KeyCode), c.ToString());
                }
                if (c >= '0' && c <= '9')
                {
                    return (KeyCode)Enum.Parse(typeof(KeyCode), "Alpha" + c);
                }
            }

            if (Enum.TryParse(key, true, out KeyCode code))
            {
                return code;
            }

            return KeyCode.None;
        }

        private static char ParseCharacter(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && key.Length == 1
                ? key[0]
                : '\0';
        }

        private static void StopSessionSafe()
        {
            lock (SessionLock)
            {
                Session.Stop();
            }
        }

        private static int ClampOrDefault(JToken token, int defaultValue, int min, int max)
        {
            try
            {
                int value = token?.ToObject<int>() ?? defaultValue;
                return Mathf.Clamp(value, min, max);
            }
            catch
            {
                return defaultValue;
            }
        }

        // NOTE: LastServerUrl was removed from CodelyIpcManager.
        // RenderStreaming sessions now require an explicit signaling_url parameter.
        // This stub returns empty so callers get a clear error from StartSession().
        private static string BuildSignalingUrlFromTauri()
        {
            return string.Empty;
        }

        [Serializable]
        private sealed class InputEventPayload
        {
            public string source = "game_view";
            public string editorWindowType = string.Empty;
            public string eventType = string.Empty;
            public double x = 0;
            public double y = 0;
            public double normalizedX = -1;
            public double normalizedY = -1;
            public int button = 0;
            public string key = string.Empty;
            public double deltaX = 0;
            public double deltaY = 0;
            public bool alt = false;
            public bool ctrl = false;
            public bool shift = false;
            public bool command = false;
        }

        private sealed class SessionOptions
        {
            public string Source;
            public string EditorWindowType;
            public int Width;
            public int Height;
            public int Fps;
            public int MinBitrateKbps;
            public int MaxBitrateKbps;
            public string SignalingUrl;
        }

        private sealed class RenderStreamingSession
        {
            private const string HostObjectName = "__CodelyWebRtcEditorHost__";

            private GameObject _hostObject;
            private Component _signalingManager;
            private Component _broadcast;
            private Component _videoSender;
            private RenderTexture _captureTexture;
            private SessionOptions _options;
            private DateTime _startedAtUtc;
            private string _lastError = string.Empty;
            private double _nextCaptureTime = 0;
            private int _capturedFrames = 0;
            private double _fpsWindowStart = 0;
            private float _runtimeCaptureFps = 0f;
            private bool _running = false;

            public static bool IsRenderStreamingAvailable(out string reason)
            {
                reason = string.Empty;
                if (ResolveType("Unity.RenderStreaming.SignalingManager, Unity.RenderStreaming") == null)
                {
                    reason = "Type Unity.RenderStreaming.SignalingManager is missing.";
                    return false;
                }
                if (ResolveType("Unity.RenderStreaming.Broadcast, Unity.RenderStreaming") == null)
                {
                    reason = "Type Unity.RenderStreaming.Broadcast is missing.";
                    return false;
                }
                if (ResolveType("Unity.RenderStreaming.VideoStreamSender, Unity.RenderStreaming") == null)
                {
                    reason = "Type Unity.RenderStreaming.VideoStreamSender is missing.";
                    return false;
                }
                if (ResolveType("Unity.RenderStreaming.WebSocketSignalingSettings, Unity.RenderStreaming") == null)
                {
                    reason = "Type Unity.RenderStreaming.WebSocketSignalingSettings is missing.";
                    return false;
                }
                return true;
            }

            public bool Start(SessionOptions options, out string error)
            {
                error = string.Empty;
                try
                {
                    Stop();

                    Type signalingManagerType = ResolveType("Unity.RenderStreaming.SignalingManager, Unity.RenderStreaming");
                    Type broadcastType = ResolveType("Unity.RenderStreaming.Broadcast, Unity.RenderStreaming");
                    Type videoSenderType = ResolveType("Unity.RenderStreaming.VideoStreamSender, Unity.RenderStreaming");
                    Type wsSettingsType = ResolveType("Unity.RenderStreaming.WebSocketSignalingSettings, Unity.RenderStreaming");
                    Type videoStreamSourceType = ResolveType("Unity.RenderStreaming.VideoStreamSource, Unity.RenderStreaming");
                    if (signalingManagerType == null || broadcastType == null || videoSenderType == null ||
                        wsSettingsType == null || videoStreamSourceType == null)
                    {
                        error = "RenderStreaming runtime types are not available.";
                        return false;
                    }

                    _hostObject = GameObject.Find(HostObjectName) ?? new GameObject(HostObjectName);
                    _hostObject.hideFlags = HideFlags.HideAndDontSave;

                    _signalingManager = EnsureComponent(_hostObject, signalingManagerType);
                    _broadcast = EnsureComponent(_hostObject, broadcastType);
                    _videoSender = EnsureComponent(_hostObject, videoSenderType);
                    if (_signalingManager == null || _broadcast == null || _videoSender == null)
                    {
                        error = "Failed to create RenderStreaming components.";
                        return false;
                    }

                    EnableRunInEditMode(_signalingManager);
                    EnableRunInEditMode(_broadcast);
                    EnableRunInEditMode(_videoSender);

                    _captureTexture = new RenderTexture(options.Width, options.Height, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "__CodelyWebRtcCaptureRT__",
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    _captureTexture.Create();

                    object textureSource = Enum.Parse(videoStreamSourceType, "Texture");
                    SetProperty(_videoSender, "source", textureSource);
                    SetProperty(_videoSender, "sourceTexture", _captureTexture);
                    InvokeMethod(_videoSender, "SetTextureSize", new Vector2Int(options.Width, options.Height));
                    InvokeMethod(_videoSender, "SetFrameRate", (float)options.Fps);
                    InvokeMethod(_videoSender, "SetBitrate", (uint)options.MinBitrateKbps, (uint)options.MaxBitrateKbps);

                    InvokeMethod(_broadcast, "AddComponent", _videoSender);

                    object signalingSettings = Activator.CreateInstance(wsSettingsType, options.SignalingUrl, null);
                    SetProperty(_signalingManager, "useDefaultSettings", false);
                    SetField(_signalingManager, "runOnAwake", false);
                    InvokeMethod(_signalingManager, "SetSignalingSettings", signalingSettings);
                    InvokeMethod(_signalingManager, "AddSignalingHandler", _broadcast);
                    InvokeMethod(_signalingManager, "Run", null, null);

                    _options = options;
                    _startedAtUtc = DateTime.UtcNow;
                    _capturedFrames = 0;
                    _fpsWindowStart = EditorApplication.timeSinceStartup;
                    _runtimeCaptureFps = 0;
                    _nextCaptureTime = 0;
                    _running = true;
                    _lastError = string.Empty;
                    EditorApplication.update += UpdateCapture;
                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    error = ex.Message;
                    Stop();
                    return false;
                }
            }

            public void Stop()
            {
                try
                {
                    EditorApplication.update -= UpdateCapture;
                }
                catch
                {
                    // Ignore update unhook errors.
                }

                try
                {
                    if (_signalingManager != null)
                    {
                        InvokeMethod(_signalingManager, "Stop");
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }

                if (_captureTexture != null)
                {
                    _captureTexture.Release();
                    UnityEngine.Object.DestroyImmediate(_captureTexture);
                    _captureTexture = null;
                }

                if (_hostObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(_hostObject);
                    _hostObject = null;
                }

                _signalingManager = null;
                _broadcast = null;
                _videoSender = null;
                _running = false;
            }

            public object BuildStatusData()
            {
                return new
                {
                    running = _running,
                    source = _options?.Source ?? "none",
                    editorWindowType = _options?.EditorWindowType ?? string.Empty,
                    signalingUrl = _options?.SignalingUrl ?? string.Empty,
                    resolution = _options == null ? null : new { width = _options.Width, height = _options.Height },
                    targetFps = _options?.Fps ?? 0,
                    runtimeCaptureFps = Math.Round(_runtimeCaptureFps, 2),
                    minBitrateKbps = _options?.MinBitrateKbps ?? 0,
                    maxBitrateKbps = _options?.MaxBitrateKbps ?? 0,
                    startedAtUtc = _startedAtUtc == default ? string.Empty : _startedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    lastError = _lastError
                };
            }

            private void UpdateCapture()
            {
                if (!_running || _captureTexture == null || _options == null)
                {
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                double interval = 1.0 / Mathf.Max(1, _options.Fps);
                if (now < _nextCaptureTime)
                {
                    return;
                }
                _nextCaptureTime = now + interval;

                bool captured;
                switch (_options.Source)
                {
                    case "scene_view": captured = CaptureSceneView(); break;
                    case "editor_window": captured = CaptureEditorWindowByType(_options.EditorWindowType); break;
                    default: captured = CaptureGameView(); break;
                }

                if (!captured)
                {
                    _lastError = $"Capture source '{_options.Source}' is unavailable.";
                    return;
                }

                _capturedFrames++;
                double elapsed = now - _fpsWindowStart;
                if (elapsed >= 1.0)
                {
                    _runtimeCaptureFps = (float)(_capturedFrames / elapsed);
                    _capturedFrames = 0;
                    _fpsWindowStart = now;
                }
            }

            private bool CaptureSceneView()
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                Camera sceneCamera = sceneView?.camera;
                if (sceneCamera == null)
                {
                    return false;
                }

                var previous = sceneCamera.targetTexture;
                sceneCamera.targetTexture = _captureTexture;
                sceneCamera.Render();
                sceneCamera.targetTexture = previous;
                return true;
            }

            private bool CaptureGameView()
            {
                Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gameViewType == null)
                {
                    return false;
                }

                EditorWindow gameView = EditorWindow.focusedWindow != null && EditorWindow.focusedWindow.GetType() == gameViewType
                    ? EditorWindow.focusedWindow
                    : Resources.FindObjectsOfTypeAll(gameViewType).OfType<EditorWindow>().FirstOrDefault();
                if (gameView == null)
                {
                    return false;
                }

                RenderTexture source = TryGetWindowRenderTexture(gameView);
                if (source == null)
                {
                    return false;
                }

                Graphics.Blit(source, _captureTexture);
                return true;
            }

            private bool CaptureEditorWindowByType(string editorWindowType)
            {
                if (string.IsNullOrWhiteSpace(editorWindowType))
                {
                    return CaptureGameView();
                }

                Type t = Type.GetType(editorWindowType);
                if (t == null)
                {
                    return false;
                }

                EditorWindow window = Resources.FindObjectsOfTypeAll(t).OfType<EditorWindow>().FirstOrDefault();
                if (window == null)
                {
                    return false;
                }

                RenderTexture source = TryGetWindowRenderTexture(window);
                if (source == null)
                {
                    return false;
                }

                Graphics.Blit(source, _captureTexture);
                return true;
            }

            private static RenderTexture TryGetWindowRenderTexture(EditorWindow window)
            {
                string[] candidates = { "targetTexture", "m_TargetTexture", "m_RenderTexture" };
                Type t = window.GetType();
                foreach (string candidate in candidates)
                {
                    var pi = t.GetProperty(candidate, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pi != null && typeof(RenderTexture).IsAssignableFrom(pi.PropertyType))
                    {
                        var rt = pi.GetValue(window) as RenderTexture;
                        if (rt != null)
                        {
                            return rt;
                        }
                    }

                    var fi = t.GetField(candidate, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null && typeof(RenderTexture).IsAssignableFrom(fi.FieldType))
                    {
                        var rt = fi.GetValue(window) as RenderTexture;
                        if (rt != null)
                        {
                            return rt;
                        }
                    }
                }
                return null;
            }

            private static Type ResolveType(string typeName)
            {
                return Type.GetType(typeName, throwOnError: false);
            }

            private static Component EnsureComponent(GameObject go, Type componentType)
            {
                Component existing = go.GetComponent(componentType);
                return existing ?? go.AddComponent(componentType);
            }

            private static void EnableRunInEditMode(Component component)
            {
                if (component is MonoBehaviour mb)
                {
                    mb.runInEditMode = true;
                    mb.enabled = true;
                }
            }

            private static void SetProperty(object target, string propertyName, object value)
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                property?.SetValue(target, value);
            }

            private static void SetField(object target, string fieldName, object value)
            {
                FieldInfo field = target.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                field?.SetValue(target, value);
            }

            private static void InvokeMethod(object target, string methodName, params object[] args)
            {
                Type t = target.GetType();
                MethodInfo method = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != methodName)
                        {
                            return false;
                        }
                        ParameterInfo[] ps = m.GetParameters();
                        return ps.Length == (args?.Length ?? 0);
                    });
                if (method == null)
                {
                    throw new MissingMethodException($"Method {methodName} not found on {t.FullName}.");
                }
                method.Invoke(target, args);
            }
        }
    }
}

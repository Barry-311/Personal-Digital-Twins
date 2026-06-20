#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Global IPC manager shared by Codely-related Editor windows.
    /// Manages one CodelyIpcClient instance and reference-counted owners.
    /// </summary>
    internal static class CodelyIpcManager
    {
        private static readonly object _lockObj = new object();
        private static readonly HashSet<string> _owners = new HashSet<string>();
        private static CodelyIpcClient _client;

        public static event Action<string> OnServerReady;
        public static event Action<bool> OnSetEmbedMode;
        public static event Action OnCloseCodelyEditorOnWorkspaceSwitch;
        public static event Action<bool> OnConnectionChanged;
        public static event Action<string> OnUpdateRestart;
        public static event Action OnDrillCompleted;

        // 最近一次 server_ready 携带的 URL。后台线程在 delayCall 入队前先写入此字段，
        // 让 CodelyWindow / DrillWindow 的 OnGUI backstop 能在主线程 update loop
        // （失焦下被节流）尚未跑通时直接拾起此 URL 完成 WebView2 初始化。
        // 字符串赋值在引用大小上原子，volatile 保证发布可见性，无需额外锁。
        private static volatile string _lastServerReadyUrl;
        public static string LastServerReadyUrl => _lastServerReadyUrl;
        internal static void SetLastServerReadyUrl(string url) => _lastServerReadyUrl = url;

        public static void Acquire(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                throw new ArgumentException("IPC owner must be non-empty", nameof(owner));
            }

            lock (_lockObj)
            {
                _owners.Add(owner);
                EnsureClientStartedLocked();
            }
        }

        public static void Release(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                return;
            }

            lock (_lockObj)
            {
                _owners.Remove(owner);
                if (_owners.Count == 0)
                {
                    StopClientLocked();
                }
            }
        }

        public static void StopAll()
        {
            lock (_lockObj)
            {
                _owners.Clear();
                StopClientLocked();
            }
        }

        public static bool TrySend(string messageType, string data = null)
        {
            lock (_lockObj)
            {
                if (_client == null || !_client.IsConnected)
                {
                    return false;
                }

                return _client.SendMessage(messageType, data);
            }
        }

        public static bool IsConnected
        {
            get
            {
                lock (_lockObj)
                {
                    return _client != null && _client.IsConnected;
                }
            }
        }

        private static void EnsureClientStartedLocked()
        {
            if (_client != null)
            {
                return;
            }

            _client = new CodelyIpcClient();
            _client.OnServerReady += HandleServerReady;
            _client.OnSetEmbedMode += HandleSetEmbedMode;
            _client.OnCloseCodelyEditorOnWorkspaceSwitch += HandleCloseCodelyEditorOnWorkspaceSwitch;
            _client.OnConnectionChanged += HandleConnectionChanged;
            _client.OnUpdateRestart += HandleUpdateRestart;
            _client.OnDrillCompleted += HandleDrillCompleted;
            _client.Start();
            CodelyLogger.Log("Codely IPC Manager: client started");
        }

        private static void StopClientLocked()
        {
            if (_client == null)
            {
                return;
            }

            var clientToDispose = _client;
            _client = null;

            try
            {
                clientToDispose.OnServerReady -= HandleServerReady;
                clientToDispose.OnSetEmbedMode -= HandleSetEmbedMode;
                clientToDispose.OnCloseCodelyEditorOnWorkspaceSwitch -= HandleCloseCodelyEditorOnWorkspaceSwitch;
                clientToDispose.OnConnectionChanged -= HandleConnectionChanged;
                clientToDispose.OnUpdateRestart -= HandleUpdateRestart;
                clientToDispose.OnDrillCompleted -= HandleDrillCompleted;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely IPC Manager: stop failed: {ex.Message}");
            }

            Task.Run(() =>
            {
                try
                {
                    clientToDispose.Dispose();
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely IPC Manager: async dispose failed: {ex.Message}");
                }
            });

            CodelyLogger.Log("Codely IPC Manager: client stop scheduled");
        }

        private static void HandleServerReady(string url) => OnServerReady?.Invoke(url);
        private static void HandleSetEmbedMode(bool embedMode) => OnSetEmbedMode?.Invoke(embedMode);
        private static void HandleCloseCodelyEditorOnWorkspaceSwitch() => OnCloseCodelyEditorOnWorkspaceSwitch?.Invoke();
        private static void HandleConnectionChanged(bool connected) => OnConnectionChanged?.Invoke(connected);
        private static void HandleUpdateRestart(string payload) => OnUpdateRestart?.Invoke(payload);
        private static void HandleDrillCompleted() => OnDrillCompleted?.Invoke();
    }
}
#endif

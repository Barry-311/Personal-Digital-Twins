using System;
using System.Collections.Generic;
using System.Text;
using Codely.Newtonsoft.Json;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Native
{
    // Managed wrapper around the unified NativeTcpBridge (NTB_*) C ABI.
    // One TCP listener serves both inbound commands and outbound notifications;
    // clients that advertise CLIENT_VERSION>=2 are eligible for notifications.
    internal static class NativeUnityTcpBridgeHost
    {
        private const int InboundBufferSize = 8 * 1024 * 1024;
        private const int ClientSnapshotBufferSize = 512 * 1024;

        private static readonly byte[] InboundBuffer = new byte[InboundBufferSize];
        private static readonly byte[] ClientSnapshotBuffer = new byte[ClientSnapshotBufferSize];

        private sealed class ClientSnapshot
        {
            public List<ClientSnapshotEntry> clients { get; set; }
        }

        private sealed class ClientSnapshotEntry
        {
            public ulong id { get; set; }
            public string endpoint { get; set; }
            public string platform { get; set; }
            public int version { get; set; }
        }

        // ---- Start / Stop ---------------------------------------------- //

        public static bool StartOrAttach(int requestedPort, int maxFrameBytes, out int boundPort)
        {
            boundPort = 0;
            if (!NativeDllLoader.IsLoaded && !NativeDllLoader.Load())
            {
                CodelyLogger.LogError("NativeTcpBridge: failed to load native library. Ensure the plugin is installed correctly.");
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                // After a domain reload the managed side restarts but the native server
                // remains bound to its existing port. Probe first so we attach to the
                // existing listener instead of relying solely on NTB_Start's idempotency.
                if (IsRunning())
                {
                    int existingPort = GetBoundPort();
                    if (existingPort > 0)
                    {
                        boundPort = existingPort;
                        return true;
                    }
                }

                int startResult = NativeUnityTcpBridgeAPI.NTB_Start(requestedPort, maxFrameBytes);
                if (startResult <= 0) return false;
                boundPort = startResult;
                return true;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"NativeTcpBridge start failed: {ex.Message}");
                return false;
            }
        }

        public static void StopServer()
        {
            if (!NativeDllLoader.IsLoaded) return;

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                NativeUnityTcpBridgeAPI.NTB_Stop();
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge stop failed: {ex.Message}");
            }
        }

        // ---- Query methods --------------------------------------------- //

        public static bool IsRunning()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_IsRunning() == 1;
#else
                return false;
#endif
            }
            catch { return false; }
        }

        public static int GetBoundPort()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return 0;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_GetBoundPort?.Invoke() ?? 0;
#else
                return 0;
#endif
            }
            catch { return 0; }
        }

        public static int GetConnectionCount()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return 0;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return Math.Max(0, NativeUnityTcpBridgeAPI.NTB_GetConnectionCount());
#else
                return 0;
#endif
            }
            catch { return 0; }
        }

        // ---- Command queue --------------------------------------------- //

        public static bool TryDequeueCommand(out ulong requestId, out string commandText)
        {
            requestId   = 0;
            commandText = null;

            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                if (NativeUnityTcpBridgeAPI.NTB_TryDequeueCommand(
                    out requestId,
                    InboundBuffer,
                    InboundBuffer.Length,
                    out int payloadBytes) != 1)
                {
                    return false;
                }

                if (payloadBytes <= 0 || payloadBytes > InboundBuffer.Length) return false;

                commandText = Encoding.UTF8.GetString(InboundBuffer, 0, payloadBytes);
                return !string.IsNullOrEmpty(commandText);
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge dequeue failed: {ex.Message}");
                return false;
            }
        }

        public static bool EnqueueResponse(ulong requestId, string responseJson)
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return false;
            }

            if (requestId == 0 || string.IsNullOrEmpty(responseJson)) return false;

            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(responseJson);
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_EnqueueResponse(requestId, payload, payload.Length) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge enqueue response failed: {ex.Message}");
                return false;
            }
        }

        // ---- Assembly reload signal ------------------------------------ //

        public static void SetIsCSharpAssemblyReloading(bool isReloading)
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                NativeUnityTcpBridgeAPI.NTB_SetIsCSharpAssemblyReloading?.Invoke(isReloading ? 1 : 0);
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge SetIsCSharpAssemblyReloading failed: {ex.Message}");
            }
        }

        // ---- Notify ---------------------------------------------------- //

        public static bool NotifyAll(string payloadJson)
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started.");
                return false;
            }

            if (string.IsNullOrEmpty(payloadJson)) return false;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payloadJson);
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_NotifyAll(bytes, bytes.Length) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge NotifyAll failed: {ex.Message}");
                return false;
            }
        }

        // ---- Connected clients ----------------------------------------- //

        public static Dictionary<string, string> GetConnectedClients()
        {
            var result = new Dictionary<string, string>();

            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return result;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                if (NativeUnityTcpBridgeAPI.NTB_GetClientsJson(
                    ClientSnapshotBuffer,
                    ClientSnapshotBuffer.Length,
                    out int bytes) != 1)
                {
                    return result;
                }

                if (bytes <= 0 || bytes > ClientSnapshotBuffer.Length) return result;

                string json = Encoding.UTF8.GetString(ClientSnapshotBuffer, 0, bytes);
                var snapshot = JsonConvert.DeserializeObject<ClientSnapshot>(json);
                if (snapshot?.clients == null) return result;

                foreach (var client in snapshot.clients)
                {
                    if (string.IsNullOrEmpty(client?.endpoint)) continue;
                    result[client.endpoint] = string.IsNullOrEmpty(client.platform) ? "unknown" : client.platform;
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge get clients failed: {ex.Message}");
            }

            return result;
        }
    }
}

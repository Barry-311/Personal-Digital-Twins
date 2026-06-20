using System;
using System.IO;
using Codely.Newtonsoft.Json;
using UnityEngine;
using UnityTcp.Editor.Native;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Helper class for status and heartbeat management
    /// </summary>
    public static class StatusHelper
    {
        /// <summary>
        /// Write heartbeat status to the main config file
        /// </summary>
        public static void WriteHeartbeat(int currentUnityPort, bool reloading, int heartbeatSeq, string reason = null)
        {
            try
            {
                // Load existing config or create new one
                var existingConfig = PortManager.GetStoredPortConfig();

                // Query the NWB signaling server port (0 if not running).
                int streamPort = 0;
                try { streamPort = NativeWindowBridgeHost.GetBoundPort(); }
                catch { /* best-effort */ }

                var portConfig = new PortManager.PortConfig
                {
                    unity_port = currentUnityPort,
                    stream_port = streamPort,
                    created_date = existingConfig?.created_date ?? DateTime.UtcNow.ToString("O"),
                    project_path = Application.dataPath,

                    // Update status fields
                    reloading = reloading,
                    reason = reason ?? (reloading ? "reloading" : "ready"),
                    seq = heartbeatSeq,
                    last_heartbeat = DateTime.UtcNow.ToString("O")
                };

                PortManager.SavePortConfig(portConfig);

                // Also maintain backwards compatibility by writing to legacy status location
                try
                {
                    // Allow override of status directory (useful in CI/containers)
                    string legacyDir = Environment.GetEnvironmentVariable("UNITY_TCP_STATUS_DIR");
                    if (string.IsNullOrWhiteSpace(legacyDir))
                    {
                        legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-tcp");
                    }
                    Directory.CreateDirectory(legacyDir);
                    string legacyFilePath = Path.Combine(legacyDir, $"unity-tcp-status-{ComputeProjectHash(Application.dataPath)}.json");
                    var legacyPayload = new
                    {
                        unity_port = currentUnityPort,
                        stream_port = streamPort,
                        reloading,
                        reason = reason ?? (reloading ? "reloading" : "ready"),
                        seq = heartbeatSeq,
                        project_path = Application.dataPath,
                        last_heartbeat = DateTime.UtcNow.ToString("O")
                    };
                    File.WriteAllText(legacyFilePath, JsonConvert.SerializeObject(legacyPayload), new System.Text.UTF8Encoding(false));
                }
                catch
                {
                    // Ignore legacy write failures
                }
            }
            catch (Exception)
            {
                // Best-effort only
            }
        }

        /// <summary>
        /// Compute a short hash of the project path for unique identification
        /// </summary>
        public static string ComputeProjectHash(string input)
        {
            try
            {
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty);
                    byte[] hashBytes = sha1.ComputeHash(bytes);
                    var sb = new System.Text.StringBuilder();
                    foreach (byte b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return sb.ToString().Substring(0, 8);
                }
            }
            catch
            {
                return "default";
            }
        }
    }
}

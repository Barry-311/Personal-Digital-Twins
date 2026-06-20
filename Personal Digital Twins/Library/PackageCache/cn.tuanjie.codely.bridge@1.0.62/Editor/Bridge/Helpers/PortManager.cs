using System;
using System.IO;
using Codely.Newtonsoft.Json;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    public static class PortManager
    {
        private const string RegistryFileName = ".com-unity-codely.json";

        [Serializable]
        public class PortConfig
        {
            public int unity_port;
            // NWB HTTP signaling server port (OS-assigned, per-instance).
            public int stream_port;
            public string created_date;
            public string project_path;

            // Status/heartbeat fields
            public bool reloading;
            public string reason;
            public int seq;
            public string last_heartbeat;
        }

        public static PortConfig GetStoredPortConfig()
        {
            try
            {
                string registryFile = GetRegistryFilePath();

                if (!File.Exists(registryFile))
                {
                    string projectLegacy = Path.Combine(GetRegistryDirectory(), "unity-tcp-port.json");
                    if (File.Exists(projectLegacy))
                    {
                        registryFile = projectLegacy;
                    }
                    else
                    {
                        string userHomeLegacy = Path.Combine(GetLegacyRegistryDirectory(), "unity-tcp-port.json");
                        if (File.Exists(userHomeLegacy))
                        {
                            registryFile = userHomeLegacy;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }

                string json = File.ReadAllText(registryFile);
                return JsonConvert.DeserializeObject<PortConfig>(json);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Could not load port config: {ex.Message}");
                return null;
            }
        }

        public static void SavePortConfig(PortConfig portConfig)
        {
            try
            {
                string registryDir = GetRegistryDirectory();
                Directory.CreateDirectory(registryDir);

                string json = JsonConvert.SerializeObject(portConfig, Formatting.Indented);
                File.WriteAllText(GetRegistryFilePath(), json, new System.Text.UTF8Encoding(false));

                try
                {
                    string legacyDir = GetLegacyRegistryDirectory();
                    Directory.CreateDirectory(legacyDir);
                    File.WriteAllText(Path.Combine(legacyDir, "unity-tcp-port.json"), json, new System.Text.UTF8Encoding(false));
                }
                catch
                {
                    // Ignore legacy write failures
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Could not save port config: {ex.Message}");
                throw;
            }
        }

        private static string GetRegistryDirectory()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return projectRoot;
        }

        private static string GetRegistryFilePath() =>
            Path.Combine(GetRegistryDirectory(), RegistryFileName);

        private static string GetLegacyRegistryDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-tcp");
    }
}

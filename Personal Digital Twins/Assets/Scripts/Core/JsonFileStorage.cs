using System;
using System.IO;
using UnityEngine;

namespace LifeOS.Core
{
    public static class JsonFileStorage
    {
        public static void Save<T>(string filePath, T data)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(filePath, json);
        }

        public static T Load<T>(string filePath) where T : new()
        {
            if (!File.Exists(filePath))
            {
                return new T();
            }

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new T();
            }

            return JsonUtility.FromJson<T>(json);
        }

        public static string GetDefaultStorageDirectory()
        {
            // Editor: <project>/SaveData
            // Build:  <exe_folder>/SaveData
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SaveData"));
        }
    }
}

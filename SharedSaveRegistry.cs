using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace ValheimSaveShare
{
    internal class SharedSaveEntry
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string GameVersion { get; set; }
        public string AddedAt { get; set; }
        public string LastSyncAt { get; set; }
    }

    /// <summary>
    /// “已经添加为共享的存档”本地列表（世界名 → GitHub 地址），持久化到
    /// BepInEx/config/ValheimSaveShare.shared.json。
    /// </summary>
    internal static class SharedSaveRegistry
    {
        private static readonly List<SharedSaveEntry> Entries = new List<SharedSaveEntry>();

        public static IReadOnlyList<SharedSaveEntry> All => Entries;

        private static string FilePath => Path.Combine(Paths.ConfigPath, "ValheimSaveShare.shared.json");

        public static void Load()
        {
            Entries.Clear();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                var data = JsonConvert.DeserializeObject<RegistryFile>(File.ReadAllText(FilePath));
                if (data?.Worlds != null)
                {
                    Entries.AddRange(data.Worlds);
                }
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("failed to load shared save registry: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(new RegistryFile { Worlds = Entries }, Formatting.Indented));
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("failed to save shared save registry: " + e.Message);
            }
        }

        public static SharedSaveEntry Find(string worldName)
        {
            return Entries.Find(e => string.Equals(e.Name, worldName, StringComparison.OrdinalIgnoreCase));
        }

        public static void AddOrUpdate(string worldName, string url, string gameVersion)
        {
            var entry = Find(worldName);
            if (entry == null)
            {
                entry = new SharedSaveEntry { Name = worldName, AddedAt = DateTime.Now.ToString("s") };
                Entries.Add(entry);
            }
            entry.Url = url;
            entry.GameVersion = gameVersion;
            entry.LastSyncAt = DateTime.Now.ToString("s");
        }

        public static bool Remove(string worldName)
        {
            return Entries.RemoveAll(e => string.Equals(e.Name, worldName, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        private class RegistryFile
        {
            public List<SharedSaveEntry> Worlds { get; set; } = new List<SharedSaveEntry>();
        }
    }
}

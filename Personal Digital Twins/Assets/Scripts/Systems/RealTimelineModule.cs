using System;
using System.Collections.Generic;
using System.Linq;
using LifeOS.Core;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    /// <summary>
    /// REAL timeline — append-only logged facts. Immutable after creation.
    /// </summary>
    public class RealTimelineModule
    {
        const string FileName = "real_timeline.json";

        readonly string _filePath;
        readonly RealTimelineDatabase _database = new RealTimelineDatabase();

        public IReadOnlyList<RealTimelineEntry> Entries => _database.entries;

        public RealTimelineModule(string storageDirectory = null)
        {
            var directory = storageDirectory ?? JsonFileStorage.GetDefaultStorageDirectory();
            _filePath = System.IO.Path.Combine(directory, FileName);
            Load();
        }

        public RealTimelineEntry AppendLogged(RealTimelineEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.eventId))
            {
                return null;
            }

            if (ContainsEventId(entry.eventId))
            {
                Debug.LogWarning($"[RealTimeline] Immutable append blocked — duplicate eventId: {entry.eventId}");
                return FindByEventId(entry.eventId);
            }

            _database.entries.Add(entry);
            Save();
            return entry;
        }

        public RealTimelineEntry LogActivity(
            string title,
            float duration,
            string category,
            DateTime? startTimeLocal = null)
        {
            var entry = RealTimelineEntry.CreateLogged(
                title,
                duration,
                category,
                startTimeLocal ?? DateTime.Now);

            return AppendLogged(entry);
        }

        public bool ContainsEventId(string eventId)
        {
            return !string.IsNullOrEmpty(eventId) &&
                   _database.entries.Any(entry => entry.eventId == eventId);
        }

        public HashSet<string> GetAllEventIds()
        {
            return new HashSet<string>(
                _database.entries
                    .Where(entry => !string.IsNullOrEmpty(entry.eventId))
                    .Select(entry => entry.eventId));
        }

        public RealTimelineEntry FindByEventId(string eventId)
        {
            return _database.entries.FirstOrDefault(entry => entry.eventId == eventId);
        }

        public List<RealTimelineEntry> GetAllOrdered()
        {
            return _database.entries
                .Where(entry => !string.IsNullOrEmpty(entry.eventId))
                .OrderBy(entry => entry.GetStartTimeLocal())
                .ToList();
        }

        public void Save()
        {
            JsonFileStorage.Save(_filePath, _database);
        }

        public void Load()
        {
            var loaded = JsonFileStorage.Load<RealTimelineDatabase>(_filePath);
            _database.entries = loaded.entries ?? new List<RealTimelineEntry>();
            MigrateLegacyEntries();
            DeduplicateStorage();
        }

        public void PrintToConsole()
        {
            var text = TimelineConsoleFormat.FormatRealTimeline(GetAllOrdered(), DateTime.Now);
            Debug.Log(text);
        }

        void MigrateLegacyEntries()
        {
            for (var i = 0; i < _database.entries.Count; i++)
            {
                var entry = _database.entries[i];
                if (string.IsNullOrEmpty(entry.eventId))
                {
                    entry.eventId = TimelineEventId.NewDirectEventId();
                }

                if (string.IsNullOrEmpty(entry.confirmedAtLocal))
                {
                    entry.confirmedAtLocal = entry.startTimeLocal;
                }
            }
        }

        void DeduplicateStorage()
        {
            var unique = _database.entries
                .Where(entry => !string.IsNullOrEmpty(entry.eventId))
                .GroupBy(entry => entry.eventId)
                .Select(group => group.First())
                .OrderBy(entry => entry.GetStartTimeLocal())
                .ToList();

            if (unique.Count != _database.entries.Count)
            {
                _database.entries = unique;
                Save();
            }
        }
    }
}

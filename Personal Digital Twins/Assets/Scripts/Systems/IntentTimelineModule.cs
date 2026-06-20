using System;
using System.Collections.Generic;
using System.Linq;
using LifeOS.Core;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    /// <summary>
    /// INTENT timeline — editable Draft / Planned entries. Never touches LifeState.
    /// </summary>
    public class IntentTimelineModule
    {
        const string FileName = "intent_timeline.json";

        readonly string _filePath;
        readonly IntentTimelineDatabase _database = new IntentTimelineDatabase();

        public IReadOnlyList<IntentTimelineEntry> Entries => _database.entries;

        public IntentTimelineModule(string storageDirectory = null)
        {
            var directory = storageDirectory ?? JsonFileStorage.GetDefaultStorageDirectory();
            _filePath = System.IO.Path.Combine(directory, FileName);
            Load();
        }

        public IntentTimelineEntry AddDraft(
            string title,
            float duration,
            string category,
            DateTime plannedStartLocal)
        {
            var entry = IntentTimelineEntry.CreateDraft(title, duration, category, plannedStartLocal);
            return TryAdd(entry) ? entry : FindByEventId(entry.eventId);
        }

        public IntentTimelineEntry AddPlanned(IntentTimelineEntry plannedEntry)
        {
            if (plannedEntry == null)
            {
                return null;
            }

            plannedEntry.status = IntentEntryStatus.Planned;
            return TryAdd(plannedEntry) ? plannedEntry : FindByEventId(plannedEntry.eventId);
        }

        public bool Update(IntentTimelineEntry updatedEntry)
        {
            var index = _database.entries.FindIndex(entry => entry.eventId == updatedEntry.eventId);
            if (index < 0)
            {
                return false;
            }

            _database.entries[index] = updatedEntry;
            Save();
            return true;
        }

        public bool PromoteToPlanned(string eventId)
        {
            var entry = FindByEventId(eventId);
            if (entry == null || entry.status != IntentEntryStatus.Draft)
            {
                return false;
            }

            entry.status = IntentEntryStatus.Planned;
            Save();
            return true;
        }

        public bool Remove(string eventId)
        {
            var removed = _database.entries.RemoveAll(entry => entry.eventId == eventId) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
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

        public IntentTimelineEntry FindByEventId(string eventId)
        {
            return _database.entries.FirstOrDefault(entry => entry.eventId == eventId);
        }

        public List<IntentTimelineEntry> GetAllOrdered()
        {
            return _database.entries
                .OrderBy(entry => entry.GetPlannedStartLocal())
                .ToList();
        }

        public List<IntentTimelineEntry> GetByStatus(IntentEntryStatus status)
        {
            return GetAllOrdered()
                .Where(entry => entry.status == status)
                .ToList();
        }

        public void Save()
        {
            JsonFileStorage.Save(_filePath, _database);
        }

        public void Load()
        {
            var loaded = JsonFileStorage.Load<IntentTimelineDatabase>(_filePath);
            _database.entries = loaded.entries ?? new List<IntentTimelineEntry>();
        }

        public HashSet<string> GetAdoptedHypotheticalIds()
        {
            var ids = new HashSet<string>();
            for (var i = 0; i < _database.entries.Count; i++)
            {
                var sourceId = _database.entries[i].sourceHypotheticalId;
                if (!string.IsNullOrEmpty(sourceId))
                {
                    ids.Add(sourceId);
                }
            }

            return ids;
        }

        public void PrintToConsole()
        {
            var text = TimelineConsoleFormat.FormatIntentTimeline(GetAllOrdered(), DateTime.Now);
            Debug.Log(text);
        }

        bool TryAdd(IntentTimelineEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.eventId) || ContainsEventId(entry.eventId))
            {
                if (entry != null)
                {
                    Debug.LogWarning($"[IntentTimeline] Duplicate eventId blocked: {entry.eventId}");
                }

                return false;
            }

            _database.entries.Add(entry);
            Save();
            return true;
        }
    }
}

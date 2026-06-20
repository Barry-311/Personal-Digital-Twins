using System;
using System.Collections.Generic;
using System.Linq;
using LifeOS.Core;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    public class LifeStateModule
    {
        const string FileName = "life_state.json";

        readonly string _filePath;
        readonly StateHistoryDatabase _database = new StateHistoryDatabase();

        public LifeState CurrentState => _database.currentState;
        public IReadOnlyList<StateSnapshot> Snapshots => _database.snapshots;

        public LifeStateModule(string storageDirectory = null)
        {
            var directory = storageDirectory ?? JsonFileStorage.GetDefaultStorageDirectory();
            _filePath = System.IO.Path.Combine(directory, FileName);
            Load();
        }

        /// <summary>
        /// LifeState = Reduce(all logged REAL events). No incremental mutation.
        /// </summary>
        public void RecomputeFromRealTimeline(IReadOnlyList<RealTimelineEntry> loggedEvents)
        {
            _database.currentState = LifeStateReducer.Reduce(loggedEvents);
            _database.snapshots = LifeStateReducer.BuildSnapshots(loggedEvents);
            Save();
        }

        public void Save()
        {
            JsonFileStorage.Save(_filePath, _database);
        }

        public void Load()
        {
            var loaded = JsonFileStorage.Load<StateHistoryDatabase>(_filePath);
            _database.currentState = loaded.currentState ?? LifeState.CreateDefault();
            _database.snapshots = loaded.snapshots ?? new List<StateSnapshot>();
        }

        public void PrintToConsole()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("<color=#CE93D8>┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓</color>");
            builder.AppendLine("<color=#CE93D8>┃  LIFE STATE        归约层 · Reduce(REAL)      ┃</color>");
            builder.AppendLine("<color=#CE93D8>┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛</color>");
            AppendStateLine(builder, "Vitality", _database.currentState.vitality);
            AppendStateLine(builder, "Focus", _database.currentState.focus);
            AppendStateLine(builder, "Stress", _database.currentState.stress);
            AppendStateLine(builder, "Fatigue", _database.currentState.fatigue);
            AppendStateLine(builder, "Social", _database.currentState.socialEnergy);

            builder.AppendLine();
            builder.AppendLine($"<color=#9E9E9E>  Snapshots: {_database.snapshots.Count} (derived from REAL events)</color>");

            if (_database.snapshots.Count > 0)
            {
                var latest = _database.snapshots.Last();
                builder.AppendLine(
                    $"<color=#9E9E9E>  Latest: {latest.GetTimestampLocal():MM-dd HH:mm} " +
                    $"after \"{latest.triggerEventTitle}\" ({FormatId(latest.triggerEventId)})</color>");
            }

            Debug.Log(builder.ToString());
        }

        static void AppendStateLine(System.Text.StringBuilder builder, string label, float value)
        {
            var filled = Mathf.RoundToInt(value / 10f);
            filled = Mathf.Clamp(filled, 0, 10);
            var bar = new string('█', filled) + new string('░', 10 - filled);
            builder.AppendLine($"  {label,-8} {value,5:F0}  [{bar}]");
        }

        static string FormatId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "????????";
            }

            return id.Length <= 8 ? id : id.Substring(0, 8) + "…";
        }
    }
}

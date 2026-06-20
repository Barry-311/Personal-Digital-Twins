using System;
using System.Collections.Generic;
using UnityEngine;
using LifeOS.Core;
using LifeOS.Data;

namespace LifeOS.Systems
{
    public class TimelineBootstrap : MonoBehaviour
    {
        [SerializeField] TimelineMode mode = TimelineMode.Real;

        TimelineCoordinator _coordinator;

        public TimelineMode Mode
        {
            get => mode;
            set => mode = value;
        }

        void Awake()
        {
            _coordinator = new TimelineCoordinator();
        }

        void Start()
        {
            SeedDemoData();
            RunSimAdoptConfirmFlow();
            PrintActiveTimeline();
            _coordinator.LifeState.PrintToConsole();
            _coordinator.SaveAll();
            Debug.Log($"[Timeline] Saved to {JsonFileStorage.GetDefaultStorageDirectory()}");
        }

        void SeedDemoData()
        {
            _coordinator.LogActivity("Morning Workout", 1.5f, "Health");
            _coordinator.LogActivity("Reading", 0.5f, "Learning", DateTime.Now.AddHours(-2));

            _coordinator.IntentTimeline.AddDraft(
                "Evening Review",
                0.5f,
                "Learning",
                DateTime.Now.AddHours(4));

            _coordinator.SimulationTimeline.SetAnchor(DateTime.Now);
            _coordinator.SimulationTimeline.SetInfluences(new List<InfluenceFactor>
            {
                new InfluenceFactor { type = "weather", magnitude = 0.4f, duration = 45f, volatility = 0.3f },
                new InfluenceFactor { type = "relationship", magnitude = 0.5f, duration = 20f, volatility = 0.1f }
            });
        }

        void RunSimAdoptConfirmFlow()
        {
            var hypotheticals = _coordinator.SimulationTimeline.GenerateSimulation(
                _coordinator.LifeState.CurrentState,
                _coordinator.IntentTimeline.GetAdoptedHypotheticalIds());

            if (hypotheticals.Count == 0)
            {
                return;
            }

            var hypo = hypotheticals[0];
            var adopted = _coordinator.AdoptFromSimulation(hypo.hypotheticalId);
            if (adopted == null)
            {
                return;
            }

            Debug.Log(
                $"[Adopt] HYPO[{hypo.hypotheticalId.Substring(0, Math.Min(8, hypo.hypotheticalId.Length))}…] " +
                $"→ Intent[{adopted.eventId.Substring(0, Math.Min(8, adopted.eventId.Length))}…]");

            var state = _coordinator.ConfirmIntent(adopted.eventId);
            if (state != null)
            {
                Debug.Log(
                    $"[Confirm] Intent → Real (new eventId) | focus={state.focus:F0} vitality={state.vitality:F0}");
            }
        }

        public void PrintActiveTimeline()
        {
            Debug.Log(TimelineConsoleFormat.FormatModeBanner(mode));

            switch (mode)
            {
                case TimelineMode.Intent:
                    _coordinator.IntentTimeline.PrintToConsole();
                    break;
                case TimelineMode.Simulation:
                    _coordinator.PrintSimulation();
                    break;
                default:
                    _coordinator.RealTimeline.PrintToConsole();
                    break;
            }
        }

        public void PrintAllTimelines()
        {
            Debug.Log(TimelineConsoleFormat.FormatModeBanner(TimelineMode.Real));
            _coordinator.RealTimeline.PrintToConsole();
            Debug.Log(TimelineConsoleFormat.FormatSeparator());
            Debug.Log(TimelineConsoleFormat.FormatModeBanner(TimelineMode.Intent));
            _coordinator.IntentTimeline.PrintToConsole();
            Debug.Log(TimelineConsoleFormat.FormatSeparator());
            Debug.Log(TimelineConsoleFormat.FormatModeBanner(TimelineMode.Simulation));
            _coordinator.PrintSimulation();
        }

        [ContextMenu("Switch To Real Mode")]
        public void SwitchToRealMode()
        {
            mode = TimelineMode.Real;
            PrintActiveTimeline();
        }

        [ContextMenu("Switch To Intent Mode")]
        public void SwitchToIntentMode()
        {
            mode = TimelineMode.Intent;
            PrintActiveTimeline();
        }

        [ContextMenu("Switch To Simulation Mode")]
        public void SwitchToSimulationMode()
        {
            mode = TimelineMode.Simulation;
            PrintActiveTimeline();
        }

        [ContextMenu("Print All Timelines")]
        public void PrintAllFromMenu()
        {
            PrintAllTimelines();
        }
    }
}

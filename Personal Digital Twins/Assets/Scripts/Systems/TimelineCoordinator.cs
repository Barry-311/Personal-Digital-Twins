using System.Collections.Generic;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    public class TimelineCoordinator
    {
        readonly RealTimelineModule _realTimeline;
        readonly IntentTimelineModule _intentTimeline;
        readonly SimulationTimelineModule _simulationTimeline;
        readonly LifeStateModule _lifeState;

        public RealTimelineModule RealTimeline => _realTimeline;
        public IntentTimelineModule IntentTimeline => _intentTimeline;
        public SimulationTimelineModule SimulationTimeline => _simulationTimeline;
        public LifeStateModule LifeState => _lifeState;

        public TimelineCoordinator(
            RealTimelineModule realTimeline = null,
            IntentTimelineModule intentTimeline = null,
            SimulationTimelineModule simulationTimeline = null,
            LifeStateModule lifeState = null)
        {
            _realTimeline = realTimeline ?? new RealTimelineModule();
            _intentTimeline = intentTimeline ?? new IntentTimelineModule();
            _simulationTimeline = simulationTimeline ?? new SimulationTimelineModule();
            _lifeState = lifeState ?? new LifeStateModule();

            SyncLifeStateFromReal();
        }

        /// <summary>Direct confirm — append REAL, then recompute state via reducer.</summary>
        public RealTimelineEntry LogActivity(
            string title,
            float duration,
            string category,
            System.DateTime? startTimeLocal = null)
        {
            var entry = _realTimeline.LogActivity(title, duration, category, startTimeLocal);
            if (entry != null)
            {
                SyncLifeStateFromReal();
            }

            return entry;
        }

        /// <summary>Hypothetical → INTENT (transformed, new eventId). No state mutation.</summary>
        public IntentTimelineEntry AdoptFromSimulation(string hypotheticalId)
        {
            var hypothetical = _simulationTimeline.FindByHypotheticalId(
                hypotheticalId,
                _lifeState.CurrentState,
                _intentTimeline.GetAdoptedHypotheticalIds());

            if (hypothetical == null)
            {
                return null;
            }

            var planned = TimelineEventTransformer.ToIntentPlanned(hypothetical);
            return _intentTimeline.AddPlanned(planned);
        }

        /// <summary>INTENT → REAL (transformed, new eventId). State via reducer only.</summary>
        public LifeState ConfirmIntent(string intentEventId)
        {
            var intent = _intentTimeline.FindByEventId(intentEventId);
            if (intent == null || intent.status != IntentEntryStatus.Planned)
            {
                return null;
            }

            var realEntry = TimelineEventTransformer.ToRealLogged(intent);
            if (_realTimeline.AppendLogged(realEntry) == null)
            {
                return null;
            }

            _intentTimeline.Remove(intentEventId);
            SyncLifeStateFromReal();
            return _lifeState.CurrentState;
        }

        public void SyncLifeStateFromReal()
        {
            _lifeState.RecomputeFromRealTimeline(_realTimeline.GetAllOrdered());
        }

        public void PrintSimulation()
        {
            _simulationTimeline.PrintToConsole(
                _lifeState.CurrentState,
                _intentTimeline.GetAdoptedHypotheticalIds());
        }

        public void SaveAll()
        {
            _realTimeline.Save();
            _intentTimeline.Save();
            _lifeState.Save();
        }
    }
}

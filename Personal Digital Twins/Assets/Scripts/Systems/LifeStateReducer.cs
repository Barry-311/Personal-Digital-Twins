using System.Collections.Generic;
using System.Linq;
using LifeOS.Data;

namespace LifeOS.Systems
{
    public static class LifeStateReducer
    {
        public static LifeState Reduce(IEnumerable<RealTimelineEntry> loggedEvents)
        {
            var state = LifeState.CreateDefault();

            foreach (var entry in OrderEvents(loggedEvents))
            {
                state.Apply(ActivityStateMutationRegistry.Resolve(entry));
            }

            return state;
        }

        public static List<StateSnapshot> BuildSnapshots(IEnumerable<RealTimelineEntry> loggedEvents)
        {
            var snapshots = new List<StateSnapshot>();
            var state = LifeState.CreateDefault();

            foreach (var entry in OrderEvents(loggedEvents))
            {
                state.Apply(ActivityStateMutationRegistry.Resolve(entry));
                snapshots.Add(StateSnapshot.Create(
                    entry.GetStartTimeLocal(),
                    state.Clone(),
                    entry));
            }

            return snapshots;
        }

        static IEnumerable<RealTimelineEntry> OrderEvents(IEnumerable<RealTimelineEntry> loggedEvents)
        {
            return loggedEvents
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.eventId))
                .OrderBy(entry => entry.GetStartTimeLocal())
                .ThenBy(entry => entry.eventId, System.StringComparer.Ordinal);
        }
    }
}

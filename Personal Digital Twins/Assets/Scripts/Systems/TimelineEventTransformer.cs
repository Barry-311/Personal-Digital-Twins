using System;
using LifeOS.Data;

namespace LifeOS.Systems
{
    /// <summary>
    /// Explicit transformations between timeline layers. Never identity mapping.
    /// </summary>
    public static class TimelineEventTransformer
    {
        public static IntentTimelineEntry ToIntentPlanned(HypotheticalEvent hypothetical)
        {
            if (hypothetical == null)
            {
                return null;
            }

            return new IntentTimelineEntry
            {
                eventId = TimelineEventId.NewDirectEventId(),
                title = hypothetical.projectedTitle,
                duration = hypothetical.projectedDurationHours,
                category = hypothetical.projectedCategory,
                plannedStartTimeLocal = hypothetical.projectedStartTimeLocal,
                status = IntentEntryStatus.Planned,
                sourceHypotheticalId = hypothetical.hypotheticalId
            };
        }

        public static RealTimelineEntry ToRealLogged(IntentTimelineEntry intent)
        {
            if (intent == null)
            {
                return null;
            }

            var now = DateTime.Now;
            return new RealTimelineEntry
            {
                eventId = TimelineEventId.NewDirectEventId(),
                title = intent.title,
                duration = intent.duration,
                category = intent.category,
                startTimeLocal = intent.plannedStartTimeLocal,
                confirmedAtLocal = now.ToString("o"),
                sourceIntentId = intent.eventId,
                sourceHypotheticalId = intent.sourceHypotheticalId ?? string.Empty
            };
        }
    }
}

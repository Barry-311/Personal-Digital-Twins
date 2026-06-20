using System;
using System.Collections.Generic;
using LifeOS.Core;

namespace LifeOS.Data
{
    /// <summary>
    /// INTENT layer: editable plans. Never mutates LifeState directly.
    /// </summary>
    [Serializable]
    public class IntentTimelineEntry
    {
        public string eventId;
        public string title;
        public float duration;
        public string category;
        public string plannedStartTimeLocal;
        public IntentEntryStatus status;
        public string sourceHypotheticalId;

        public static IntentTimelineEntry CreateDraft(
            string title,
            float duration,
            string category,
            DateTime plannedStartLocal)
        {
            return new IntentTimelineEntry
            {
                eventId = TimelineEventId.NewDirectEventId(),
                title = title,
                duration = duration,
                category = category,
                plannedStartTimeLocal = LifeTime.FormatLocal(plannedStartLocal),
                status = IntentEntryStatus.Draft,
                sourceHypotheticalId = string.Empty
            };
        }

        public DateTime GetPlannedStartLocal()
        {
            return LifeTime.ParseLocal(plannedStartTimeLocal);
        }

        public void SetPlannedStartLocal(DateTime time)
        {
            plannedStartTimeLocal = LifeTime.FormatLocal(time);
        }
    }

    [Serializable]
    public class IntentTimelineDatabase
    {
        public List<IntentTimelineEntry> entries = new List<IntentTimelineEntry>();
    }
}

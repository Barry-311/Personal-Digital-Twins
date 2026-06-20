using System;
using System.Collections.Generic;

namespace LifeOS.Data
{
    /// <summary>
    /// REAL layer: immutable confirmed facts. Source of truth for state reduction.
    /// </summary>
    [Serializable]
    public class RealTimelineEntry
    {
        public string eventId;
        public string title;
        public float duration;
        public string category;
        public string startTimeLocal;
        public string confirmedAtLocal;

        public string sourceIntentId;
        public string sourceHypotheticalId;

        public static RealTimelineEntry CreateLogged(
            string title,
            float duration,
            string category,
            DateTime startTimeLocal)
        {
            var now = DateTime.Now;
            return new RealTimelineEntry
            {
                eventId = TimelineEventId.NewDirectEventId(),
                title = title,
                duration = duration,
                category = category,
                startTimeLocal = startTimeLocal.ToString("o"),
                confirmedAtLocal = now.ToString("o"),
                sourceIntentId = string.Empty,
                sourceHypotheticalId = string.Empty
            };
        }

        public DateTime GetStartTimeLocal()
        {
            return DateTime.Parse(startTimeLocal, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }
    }

    [Serializable]
    public class RealTimelineDatabase
    {
        public List<RealTimelineEntry> entries = new List<RealTimelineEntry>();
    }
}

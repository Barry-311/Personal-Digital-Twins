using System;
using LifeOS.Core;

namespace LifeOS.Data
{
    [Serializable]
    public class StateSnapshot
    {
        public string timestampLocal;
        public LifeState state = new LifeState();
        public string triggerEventId;
        public string triggerEventTitle;

        public static StateSnapshot Create(
            DateTime timestampLocal,
            LifeState state,
            RealTimelineEntry triggerEvent)
        {
            return new StateSnapshot
            {
                timestampLocal = LifeTime.FormatLocal(timestampLocal),
                state = state.Clone(),
                triggerEventId = triggerEvent?.eventId ?? string.Empty,
                triggerEventTitle = triggerEvent?.title ?? string.Empty
            };
        }

        public DateTime GetTimestampLocal()
        {
            return LifeTime.ParseLocal(timestampLocal);
        }
    }
}

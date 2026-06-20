using System;

namespace LifeOS.Data
{
    public static class TimelineEventId
    {
        public static string NewDirectEventId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}

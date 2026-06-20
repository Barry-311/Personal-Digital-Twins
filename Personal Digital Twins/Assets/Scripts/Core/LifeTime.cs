using System;

namespace LifeOS.Core
{
    public static class LifeTime
    {
        public static string FormatLocal(DateTime time)
        {
            return time.ToString("o");
        }

        public static DateTime ParseLocal(string value)
        {
            return DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        public static DateTime Now => DateTime.Now;
    }
}

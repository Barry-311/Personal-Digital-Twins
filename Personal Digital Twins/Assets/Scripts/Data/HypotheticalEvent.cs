using System;
using System.Collections.Generic;
using LifeOS.Core;

namespace LifeOS.Data
{
    /// <summary>
    /// SIM layer output — purely predictive, never becomes REAL by identity.
    /// </summary>
    [Serializable]
    public class HypotheticalEvent
    {
        public string hypotheticalId;
        public string scenarioKey;
        public string title;
        public string category;
        public float durationHours;

        public float offsetMinutes;
        public float probability;
        public string projectedTitle;
        public string projectedCategory;
        public float projectedDurationHours;
        public string projectedStartTimeLocal;

        public List<InfluenceFactor> appliedInfluences = new List<InfluenceFactor>();

        public DateTime GetProjectedStartLocal()
        {
            return LifeTime.ParseLocal(projectedStartTimeLocal);
        }
    }
}

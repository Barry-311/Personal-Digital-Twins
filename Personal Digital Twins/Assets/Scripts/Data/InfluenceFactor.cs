using System;

namespace LifeOS.Data
{
    [Serializable]
    public class InfluenceFactor
    {
        public string type;
        public float magnitude;
        public float duration;
        public float volatility;
    }
}

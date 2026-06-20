namespace LifeOS.Data
{
    [System.Serializable]
    public struct StateMutation
    {
        public float vitality;
        public float focus;
        public float stress;
        public float fatigue;
        public float socialEnergy;

        public static StateMutation Zero => default;
    }
}

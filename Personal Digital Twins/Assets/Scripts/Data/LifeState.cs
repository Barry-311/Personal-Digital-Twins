using System;
using UnityEngine;

namespace LifeOS.Data
{
    [Serializable]
    public class LifeState
    {
        public float vitality;
        public float focus;
        public float stress;
        public float fatigue;
        public float socialEnergy;

        public static LifeState CreateDefault()
        {
            return new LifeState
            {
                vitality = 50f,
                focus = 50f,
                stress = 20f,
                fatigue = 20f,
                socialEnergy = 50f
            };
        }

        public void Apply(StateMutation mutation)
        {
            vitality = ClampStat(vitality + mutation.vitality);
            focus = ClampStat(focus + mutation.focus);
            stress = ClampStat(stress + mutation.stress);
            fatigue = ClampStat(fatigue + mutation.fatigue);
            socialEnergy = ClampStat(socialEnergy + mutation.socialEnergy);
        }

        public LifeState Clone()
        {
            return new LifeState
            {
                vitality = vitality,
                focus = focus,
                stress = stress,
                fatigue = fatigue,
                socialEnergy = socialEnergy
            };
        }

        static float ClampStat(float value)
        {
            return Mathf.Clamp(value, 0f, 100f);
        }
    }
}

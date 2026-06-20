using System.Collections.Generic;
using LifeOS.Data;

namespace LifeOS.Systems
{
    public static class ActivityStateMutationRegistry
    {
        static readonly Dictionary<string, StateMutation> MutationsByKey =
            new Dictionary<string, StateMutation>
            {
                ["workout"] = new StateMutation
                {
                    vitality = 18f,
                    stress = -5f,
                    fatigue = 8f
                },
                ["deep work"] = new StateMutation
                {
                    focus = 15f,
                    stress = 4f,
                    fatigue = 10f
                },
                ["reading"] = new StateMutation
                {
                    focus = 8f,
                    stress = -3f,
                    fatigue = 2f
                },
                ["lunch break"] = new StateMutation
                {
                    vitality = 5f,
                    stress = -4f,
                    fatigue = -6f,
                    socialEnergy = 3f
                }
            };

        public static StateMutation Resolve(RealTimelineEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.title))
            {
                return StateMutation.Zero;
            }

            var normalizedTitle = entry.title.ToLowerInvariant();

            foreach (var pair in MutationsByKey)
            {
                if (normalizedTitle.Contains(pair.Key))
                {
                    return pair.Value;
                }
            }

            return StateMutation.Zero;
        }
    }
}

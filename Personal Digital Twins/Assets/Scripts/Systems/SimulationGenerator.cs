using System;
using System.Collections.Generic;
using System.Linq;
using LifeOS.Core;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    public static class SimulationGenerator
    {
        struct ScenarioTemplate
        {
            public string key;
            public string title;
            public float durationHours;
            public string category;
            public float baseOffsetMinutes;
        }

        static readonly ScenarioTemplate[] Scenarios =
        {
            new ScenarioTemplate
            {
                key = "deep_work",
                title = "Deep Work",
                durationHours = 2f,
                category = "Work",
                baseOffsetMinutes = 60f
            },
            new ScenarioTemplate
            {
                key = "lunch_break",
                title = "Lunch Break",
                durationHours = 0.5f,
                category = "Rest",
                baseOffsetMinutes = 180f
            }
        };

        public static List<HypotheticalEvent> GenerateSimulation(
            DateTime anchorLocal,
            LifeState lifeState,
            IList<InfluenceFactor> influences)
        {
            if (lifeState == null)
            {
                lifeState = LifeState.CreateDefault();
            }

            var mergedInfluences = MergeInfluences(lifeState, influences);
            var events = new List<HypotheticalEvent>(Scenarios.Length);

            foreach (var scenario in Scenarios)
            {
                events.Add(ProjectScenario(anchorLocal, lifeState, scenario, mergedInfluences));
            }

            return events
                .OrderBy(e => e.offsetMinutes)
                .ThenBy(e => e.scenarioKey, StringComparer.Ordinal)
                .ToList();
        }

        static HypotheticalEvent ProjectScenario(
            DateTime anchorLocal,
            LifeState lifeState,
            ScenarioTemplate scenario,
            List<InfluenceFactor> influences)
        {
            var offsetMinutes = scenario.baseOffsetMinutes;
            var probability = 0.85f;
            var durationHours = scenario.durationHours;
            var title = scenario.title;
            var category = scenario.category;
            var volatilityPenalty = 0f;

            offsetMinutes += lifeState.fatigue * 0.3f;
            offsetMinutes += lifeState.stress * 0.15f;
            offsetMinutes -= lifeState.vitality * 0.05f;
            probability -= lifeState.stress * 0.002f;
            probability -= lifeState.fatigue * 0.001f;
            probability += lifeState.focus * 0.001f;

            foreach (var factor in influences)
            {
                ApplyFactor(factor, ref offsetMinutes, ref probability, ref durationHours, ref title, ref category, ref volatilityPenalty);
            }

            probability = Mathf.Clamp01(probability - volatilityPenalty);
            durationHours = Mathf.Max(0.1f, durationHours);
            offsetMinutes = Mathf.Max(0f, offsetMinutes);

            var projectedStart = anchorLocal.AddMinutes(offsetMinutes);

            return new HypotheticalEvent
            {
                hypotheticalId = BuildHypotheticalId(anchorLocal, lifeState, scenario.key),
                scenarioKey = scenario.key,
                title = scenario.title,
                category = scenario.category,
                durationHours = scenario.durationHours,
                offsetMinutes = offsetMinutes,
                probability = probability,
                projectedTitle = title,
                projectedCategory = category,
                projectedDurationHours = durationHours,
                projectedStartTimeLocal = LifeTime.FormatLocal(projectedStart),
                appliedInfluences = influences.Select(CloneFactor).ToList()
            };
        }

        static List<InfluenceFactor> MergeInfluences(LifeState lifeState, IList<InfluenceFactor> external)
        {
            var merged = new List<InfluenceFactor>();

            if (external != null)
            {
                foreach (var factor in external)
                {
                    merged.Add(CloneFactor(factor));
                }
            }

            if (lifeState.fatigue > 40f)
            {
                merged.Add(new InfluenceFactor
                {
                    type = "fatigue",
                    magnitude = lifeState.fatigue / 100f,
                    duration = 60f,
                    volatility = 0.2f
                });
            }

            if (lifeState.stress > 30f)
            {
                merged.Add(new InfluenceFactor
                {
                    type = "stress",
                    magnitude = lifeState.stress / 100f,
                    duration = 30f,
                    volatility = 0.15f
                });
            }

            return merged;
        }

        static InfluenceFactor CloneFactor(InfluenceFactor source)
        {
            return new InfluenceFactor
            {
                type = source.type,
                magnitude = source.magnitude,
                duration = source.duration,
                volatility = source.volatility
            };
        }

        static void ApplyFactor(
            InfluenceFactor factor,
            ref float offsetMinutes,
            ref float probability,
            ref float durationHours,
            ref string title,
            ref string category,
            ref float volatilityPenalty)
        {
            if (factor == null || string.IsNullOrEmpty(factor.type))
            {
                return;
            }

            var type = factor.type.ToLowerInvariant();
            var impact = factor.magnitude * factor.duration;

            switch (type)
            {
                case "fatigue":
                    offsetMinutes += impact * 0.5f;
                    probability -= factor.magnitude * 0.15f;
                    durationHours *= 1f - factor.magnitude * 0.1f;
                    break;

                case "stress":
                    offsetMinutes += impact * 0.2f;
                    probability -= factor.magnitude * 0.25f;
                    break;

                case "relationship":
                    offsetMinutes -= impact * 0.15f;
                    if (factor.magnitude > 0.5f)
                    {
                        title = $"{title} (social shift)";
                    }
                    break;

                case "weather":
                    offsetMinutes += impact * 0.3f;
                    probability -= factor.magnitude * 0.1f;
                    if (factor.magnitude > 0.6f)
                    {
                        category = $"{category}/disrupted";
                    }
                    break;

                default:
                    offsetMinutes += impact * 0.1f;
                    break;
            }

            volatilityPenalty += factor.volatility * 0.05f;
        }

        static string BuildHypotheticalId(DateTime anchorLocal, LifeState lifeState, string scenarioKey)
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + anchorLocal.Ticks.GetHashCode();
                hash = (hash * 31) + scenarioKey.GetHashCode();
                hash = (hash * 31) + Mathf.RoundToInt(lifeState.vitality);
                hash = (hash * 31) + Mathf.RoundToInt(lifeState.focus);
                hash = (hash * 31) + Mathf.RoundToInt(lifeState.stress);
                hash = (hash * 31) + Mathf.RoundToInt(lifeState.fatigue);
                hash = (hash * 31) + Mathf.RoundToInt(lifeState.socialEnergy);
                return $"hypo_{scenarioKey}_{hash:X8}";
            }
        }
    }
}

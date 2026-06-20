using System;
using System.Collections.Generic;
using LifeOS.Core;
using LifeOS.Data;
using UnityEngine;

namespace LifeOS.Systems
{
    /// <summary>
    /// SIM layer — stateless HypotheticalEvent projection. Never touches REAL objects.
    /// </summary>
    public class SimulationTimelineModule
    {
        DateTime _anchorLocal;
        List<InfluenceFactor> _influences = new List<InfluenceFactor>();

        public DateTime AnchorLocal => _anchorLocal;

        public SimulationTimelineModule()
        {
            ResetAnchorToNow();
        }

        public void ResetAnchorToNow()
        {
            _anchorLocal = DateTime.Now;
        }

        public void SetAnchor(DateTime anchorLocal)
        {
            _anchorLocal = anchorLocal;
        }

        public void SetInfluences(List<InfluenceFactor> influences)
        {
            _influences = influences ?? new List<InfluenceFactor>();
        }

        public List<HypotheticalEvent> GenerateSimulation(
            LifeState lifeState,
            HashSet<string> adoptedHypotheticalIds = null)
        {
            var events = SimulationGenerator.GenerateSimulation(_anchorLocal, lifeState, _influences);
            return FilterAdopted(events, adoptedHypotheticalIds);
        }

        public HypotheticalEvent FindByHypotheticalId(
            string hypotheticalId,
            LifeState lifeState,
            HashSet<string> adoptedHypotheticalIds = null)
        {
            var events = GenerateSimulation(lifeState, adoptedHypotheticalIds);
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].hypotheticalId == hypotheticalId)
                {
                    return events[i];
                }
            }

            return null;
        }

        public void PrintToConsole(LifeState lifeState, HashSet<string> adoptedHypotheticalIds = null)
        {
            var events = GenerateSimulation(lifeState, adoptedHypotheticalIds);
            var text = TimelineConsoleFormat.FormatSimulationTimeline(
                events,
                _anchorLocal,
                DateTime.Now,
                lifeState);

            Debug.Log(text);
        }

        static List<HypotheticalEvent> FilterAdopted(
            List<HypotheticalEvent> events,
            HashSet<string> adoptedHypotheticalIds)
        {
            if (adoptedHypotheticalIds == null || adoptedHypotheticalIds.Count == 0)
            {
                return events;
            }

            var filtered = new List<HypotheticalEvent>(events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                if (!adoptedHypotheticalIds.Contains(events[i].hypotheticalId))
                {
                    filtered.Add(events[i]);
                }
            }

            return filtered;
        }
    }
}

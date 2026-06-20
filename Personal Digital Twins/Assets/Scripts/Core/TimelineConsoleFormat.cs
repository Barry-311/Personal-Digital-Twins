using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LifeOS.Data;

namespace LifeOS.Core
{
    public static class TimelineConsoleFormat
    {
        const string RealColor = "#4FC3F7";
        const string IntentColor = "#AED581";
        const string SimColor = "#FFB74D";
        const string MutedColor = "#9E9E9E";
        const string FactColor = "#81C784";
        const string DraftColor = "#FFD54F";
        const string PlannedColor = "#FFCC80";

        public static string FormatRealTimeline(IReadOnlyList<RealTimelineEntry> entries, DateTime deviceNow)
        {
            var builder = new StringBuilder();
            AppendRealHeader(builder, deviceNow);

            if (entries.Count == 0)
            {
                builder.AppendLine(C(MutedColor, "  (empty — only LOGGED facts belong here)"));
                return builder.ToString();
            }

            builder.AppendLine(C(MutedColor, "  ─── LOGGED FACTS (immutable · LifeState source) ───"));
            foreach (var entry in entries)
            {
                AppendRealEntry(builder, entry);
            }

            builder.AppendLine();
            builder.AppendLine(C(MutedColor, $"  Summary: {entries.Count} logged fact(s)"));
            return builder.ToString();
        }

        public static string FormatIntentTimeline(IReadOnlyList<IntentTimelineEntry> entries, DateTime deviceNow)
        {
            var builder = new StringBuilder();
            AppendIntentHeader(builder, deviceNow);

            if (entries.Count == 0)
            {
                builder.AppendLine(C(MutedColor, "  (empty — add Draft or Planned intents)"));
                return builder.ToString();
            }

            var drafts = entries.Count(e => e.status == IntentEntryStatus.Draft);
            var planned = entries.Count(e => e.status == IntentEntryStatus.Planned);

            builder.AppendLine(C(MutedColor, "  ─── PLANS (editable · no LifeState mutation) ───"));
            foreach (var entry in entries)
            {
                AppendIntentEntry(builder, entry);
            }

            builder.AppendLine();
            builder.AppendLine(C(MutedColor,
                $"  Summary: {entries.Count} total | {drafts} draft | {planned} planned"));
            return builder.ToString();
        }

        public static string FormatSimulationTimeline(
            IReadOnlyList<HypotheticalEvent> events,
            DateTime anchorLocal,
            DateTime deviceNow,
            LifeState lifeState)
        {
            var builder = new StringBuilder();
            AppendSimHeader(builder, anchorLocal, deviceNow, lifeState);

            if (events.Count == 0)
            {
                builder.AppendLine(C(MutedColor, "  (no projections — all reserved or empty state)"));
                return builder.ToString();
            }

            builder.AppendLine(C(MutedColor, "  ─── PROJECTIONS (stateless · not stored) ───"));
            for (var i = 0; i < events.Count; i++)
            {
                AppendSimEntry(builder, events[i], i + 1);
            }

            builder.AppendLine();
            builder.AppendLine(C(MutedColor,
                $"  Summary: {events.Count} projection(s) | adopt → INTENT only"));
            return builder.ToString();
        }

        public static string FormatModeBanner(TimelineMode mode)
        {
            switch (mode)
            {
                case TimelineMode.Intent:
                    return C(IntentColor, "▶ VIEWING: INTENT — editable Draft / Planned (no LifeState)");
                case TimelineMode.Simulation:
                    return C(SimColor, "▶ VIEWING: SIMULATION — stateless projection from LifeState");
                default:
                    return C(RealColor, "▶ VIEWING: REAL — immutable logged facts (source of truth)");
            }
        }

        public static string FormatSeparator()
        {
            return C(MutedColor, "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");
        }

        static void AppendRealHeader(StringBuilder builder, DateTime deviceNow)
        {
            builder.AppendLine(C(RealColor, "┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓"));
            builder.AppendLine(C(RealColor, "┃  REAL TIMELINE     真实 · 唯一事实源           ┃"));
            builder.AppendLine(C(RealColor, "┃  LOGGED only · immutable · mutates LifeState  ┃"));
            builder.AppendLine(C(RealColor, "┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛"));
            builder.AppendLine(C(MutedColor, $"  Device now: {deviceNow:yyyy-MM-dd HH:mm:ss}"));
            builder.AppendLine();
        }

        static void AppendIntentHeader(StringBuilder builder, DateTime deviceNow)
        {
            builder.AppendLine(C(IntentColor, "┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓"));
            builder.AppendLine(C(IntentColor, "┃  INTENT TIMELINE   计划 · 可编辑               ┃"));
            builder.AppendLine(C(IntentColor, "┃  Draft / Planned · confirm → REAL              ┃"));
            builder.AppendLine(C(IntentColor, "┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛"));
            builder.AppendLine(C(MutedColor, $"  Device now: {deviceNow:yyyy-MM-dd HH:mm:ss}"));
            builder.AppendLine();
        }

        static void AppendSimHeader(StringBuilder builder, DateTime anchorLocal, DateTime deviceNow, LifeState lifeState)
        {
            builder.AppendLine(C(SimColor, "┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓"));
            builder.AppendLine(C(SimColor, "┃  SIMULATION        假设 · HypotheticalEvent     ┃"));
            builder.AppendLine(C(SimColor, "┃  predictive only · adopt transforms → INTENT   ┃"));
            builder.AppendLine(C(SimColor, "┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛"));
            builder.AppendLine(C(MutedColor, $"  Anchor:     {anchorLocal:yyyy-MM-dd HH:mm:ss}"));
            builder.AppendLine(C(MutedColor, $"  Device now: {deviceNow:yyyy-MM-dd HH:mm:ss}"));
            if (lifeState != null)
            {
                builder.AppendLine(C(MutedColor,
                    $"  LifeState:  focus={lifeState.focus:F0} stress={lifeState.stress:F0} " +
                    $"fatigue={lifeState.fatigue:F0} vitality={lifeState.vitality:F0}"));
            }

            builder.AppendLine();
        }

        static void AppendRealEntry(StringBuilder builder, RealTimelineEntry entry)
        {
            var time = entry.GetStartTimeLocal();
            builder.AppendLine(
                $"  {C(FactColor, "● LOGGED")}  {C(MutedColor, $"[{FormatEventId(entry.eventId)}]")}  " +
                $"{C(RealColor, time.ToString("MM-dd HH:mm"))}  " +
                $"{entry.title}  ·  {entry.category}  ·  {entry.duration:F1}h");
        }

        static void AppendIntentEntry(StringBuilder builder, IntentTimelineEntry entry)
        {
            var statusTag = entry.status == IntentEntryStatus.Draft
                ? C(DraftColor, "◌ DRAFT  ")
                : C(PlannedColor, "◎ PLANNED");

            builder.AppendLine(
                $"  {statusTag}  {C(MutedColor, $"[{FormatEventId(entry.eventId)}]")}  " +
                $"{C(IntentColor, entry.GetPlannedStartLocal().ToString("MM-dd HH:mm"))}  " +
                $"{entry.title}  ·  {entry.category}  ·  {entry.duration:F1}h");
        }

        static void AppendSimEntry(StringBuilder builder, HypotheticalEvent hypothetical, int index)
        {
            var probBar = BuildProbabilityBar(hypothetical.probability);
            builder.AppendLine(
                C(SimColor, $"  ◇ HYPO #{index}") +
                C(MutedColor, $"  [{FormatEventId(hypothetical.hypotheticalId)}]  ") +
                C(MutedColor, $"intent: {hypothetical.title}  ·  {hypothetical.category}  ·  {hypothetical.durationHours:F1}h"));
            builder.AppendLine(
                C(SimColor, $"     ├─ projected: ") +
                C(MutedColor, $"{hypothetical.GetProjectedStartLocal():MM-dd HH:mm}") +
                C(SimColor, $"  (~{hypothetical.offsetMinutes:F0}min from anchor)"));
            builder.AppendLine(
                C(SimColor, $"     ├─ mutated:   ") +
                C(MutedColor, $"{hypothetical.projectedTitle}  ·  {hypothetical.projectedCategory}  ·  {hypothetical.projectedDurationHours:F1}h"));
            builder.AppendLine(
                C(SimColor, $"     ├─ probability: ") +
                C(MutedColor, $"{hypothetical.probability:P0}") +
                $"  {probBar}");
            builder.AppendLine(
                C(SimColor, $"     └─ influences: ") +
                C(MutedColor, FormatInfluences(hypothetical.appliedInfluences)));
            builder.AppendLine();
        }

        static string FormatInfluences(List<InfluenceFactor> influences)
        {
            if (influences == null || influences.Count == 0)
            {
                return "(none)";
            }

            return string.Join(" | ", influences.Select(f =>
                $"{f.type}[mag={f.magnitude:F1} dur={f.duration:F0} vol={f.volatility:F1}]"));
        }

        static string BuildProbabilityBar(float probability)
        {
            var filled = (int)Math.Round(probability * 10f);
            filled = Math.Max(0, Math.Min(10, filled));
            return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
        }

        static string FormatEventId(string eventId)
        {
            if (string.IsNullOrEmpty(eventId))
            {
                return "????????";
            }

            return eventId.Length <= 8 ? eventId : eventId.Substring(0, 8) + "…";
        }

        static string C(string hex, string text)
        {
            return $"<color={hex}>{text}</color>";
        }
    }
}

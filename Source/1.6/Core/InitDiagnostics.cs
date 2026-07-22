using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Passive recorder for Initialize-phase failures and the printer for the
    // startup summary. Each subsystem's Initialize method wraps its own work
    // and calls RecordFailure when it catches an exception, so a single failed
    // subsystem (e.g. a modded def breaking one Initialize) doesn't prevent the
    // rest of the mod from coming online with degraded functionality.
    // LogSummary surfaces failed phases alongside the per-mod def counts so the
    // player sees one diagnostic line per startup.
    public sealed class InitDiagnostics
    {
        private readonly List<string> failedPhases = new List<string>();
        private readonly List<string> phaseTimings = new List<string>();

        // Runs from construction until the summary line is built, so it covers
        // the whole init block — including any glue between timed phases.
        private readonly Stopwatch totalTimer = Stopwatch.StartNew();

        public void RecordFailure(string name, Exception ex)
        {
            Log.Error("[Unique Weapons Unbound] " + name + ".Initialize failed: " + ex);
            failedPhases.Add(name);
        }

        // Times one init phase for the startup summary. Exceptions propagate
        // unchanged: subsystem Initialize methods already catch internally and
        // RecordFailure, and anything else throwing here would have aborted the
        // static ctor before this existed too.
        public void Time(string name, Action work)
        {
            var sw = Stopwatch.StartNew();
            work();
            sw.Stop();
            phaseTimings.Add(name + " " + sw.ElapsedMilliseconds + "ms");
        }

        public void LogSummary()
        {
            try { LogSummaryInner(); }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Init diagnostic failed: " + ex);
            }
        }

        private void LogSummaryInner()
        {
            var pairsByMod = GroupBySourceMod(WeaponRegistry.AllUniqueDefs);
            var orphansByMod = GroupBySourceMod(WeaponRegistry.OrphanUniqueDefs);
            var traitsByMod = GroupBySourceMod(DefDatabase<WeaponTraitDef>.AllDefs);
            var rulesByMod = GroupBySourceMod(TraitCostUtility.CachedRules);

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            var sb = new StringBuilder();
            sb.Append("[Unique Weapons Unbound] v").Append(version).AppendLine(" initialized");
            // Answers "is this mod slowing my startup?" without dev tooling.
            // Covers the ModInitializer block only — XML load and the texture/
            // reflection static ctors run outside it, but they're trivial.
            sb.Append("  Startup init took ").Append(totalTimer.ElapsedMilliseconds).Append("ms");
            if (phaseTimings.Count > 0)
                sb.Append(" (").Append(string.Join(", ", phaseTimings)).Append(')');
            sb.AppendLine();
            if (failedPhases.Count > 0)
            {
                sb.Append("  Initialization phases failed: ")
                    .AppendLine(string.Join(", ", failedPhases));
                sb.AppendLine("  (see preceding errors; counts below may be empty or partial)");
            }
            AppendCategory(sb, "Weapon Pairs", pairsByMod);
            // Only surface the orphan row when there's something to surface —
            // a "(0): none" line for the typical clean-install case would be
            // noise. Per-orphan warnings are emitted by WeaponRegistry itself.
            if (orphansByMod.Values.Sum() > 0)
                AppendCategory(sb, "Orphan Unique Weapons", orphansByMod);
            AppendCategory(sb, "Weapon Traits", traitsByMod);
            AppendCategory(sb, "Trait Cost Rules", rulesByMod);
            Log.Message(sb.ToString().TrimEnd());
        }

        private static Dictionary<string, int> GroupBySourceMod(IEnumerable<Def> defs)
        {
            var counts = new Dictionary<string, int>();
            if (defs == null)
                return counts;
            foreach (Def def in defs)
            {
                string sourceName = def.modContentPack?.Name ?? "(unknown)";
                counts.TryGetValue(sourceName, out int existing);
                counts[sourceName] = existing + 1;
            }
            return counts;
        }

        private static void AppendCategory(StringBuilder sb, string label, Dictionary<string, int> counts)
        {
            int total = counts.Values.Sum();
            sb.Append("  ").Append(label).Append(" (").Append(total).Append("): ");
            if (total == 0)
            {
                sb.AppendLine("none");
                return;
            }
            // Descending by count, then alphabetical for stable output across runs.
            bool first = true;
            foreach (var entry in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            {
                if (!first) sb.Append(", ");
                sb.Append(entry.Key).Append(" (").Append(entry.Value).Append(')');
                first = false;
            }
            sb.AppendLine();
        }
    }
}

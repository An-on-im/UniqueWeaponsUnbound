using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Passive recorder for Initialize-phase failures and the printer for the
    /// startup summary. Each subsystem's Initialize method wraps its own work
    /// and calls <see cref="RecordFailure"/> when it catches an exception, so
    /// a single failed subsystem (e.g. a modded def breaking one Initialize)
    /// doesn't prevent the rest of the mod from coming online with degraded
    /// functionality. <see cref="LogSummary"/> surfaces failed phases
    /// alongside the per-mod def counts so the player sees one diagnostic
    /// line per startup.
    /// </summary>
    public sealed class InitDiagnostics
    {
        private readonly List<string> failedPhases = new List<string>();

        public void RecordFailure(string name, Exception ex)
        {
            Log.Error("[Unique Weapons Unbound] " + name + ".Initialize failed: " + ex);
            failedPhases.Add(name);
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
            var traitsByMod = GroupBySourceMod(DefDatabase<WeaponTraitDef>.AllDefs);
            var rulesByMod = GroupBySourceMod(TraitCostUtility.CachedRules);

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            var sb = new StringBuilder();
            sb.Append("[Unique Weapons Unbound] v").Append(version).AppendLine(" initialized");
            if (failedPhases.Count > 0)
            {
                sb.Append("  Initialization phases failed: ")
                    .AppendLine(string.Join(", ", failedPhases));
                sb.AppendLine("  (see preceding errors; counts below may be empty or partial)");
            }
            AppendCategory(sb, "Weapon Pairs", pairsByMod);
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

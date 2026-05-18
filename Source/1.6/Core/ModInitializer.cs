using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    [StaticConstructorOnStartup]
    public static class UniqueWeaponsUnboundMod
    {
        static UniqueWeaponsUnboundMod()
        {
            var harmony = new Harmony("shunter.uniqueweaponsunbound");
            harmony.PatchAll();

            WeaponRegistry.Initialize();
            WorkbenchUtility.Initialize();
            TraitCostUtility.Initialize();

            LogInitDiagnostic();
        }

        private static void LogInitDiagnostic()
        {
            var pairsByMod = GroupBySourceMod(WeaponRegistry.AllUniqueDefs);
            var traitsByMod = GroupBySourceMod(DefDatabase<WeaponTraitDef>.AllDefs);
            var rulesByMod = GroupBySourceMod(TraitCostUtility.CachedRules);

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            var sb = new StringBuilder();
            sb.Append("[Unique Weapons Unbound] v").Append(version).AppendLine(" initialized");
            AppendCategory(sb, "Weapon Pairs", pairsByMod);
            AppendCategory(sb, "Weapon Traits", traitsByMod);
            AppendCategory(sb, "Trait Cost Rules", rulesByMod);
            Log.Message(sb.ToString().TrimEnd());
        }

        private static Dictionary<string, int> GroupBySourceMod(IEnumerable<Def> defs)
        {
            var counts = new Dictionary<string, int>();
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

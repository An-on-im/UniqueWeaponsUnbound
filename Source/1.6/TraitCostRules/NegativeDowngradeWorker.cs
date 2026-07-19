using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Downgrades cost materials by one tech level for negative traits when
    // adding. Skipped for removal — removing a negative trait requires
    // proper-tier materials to restore the weapon to standard quality.
    public class NegativeDowngradeWorker : TraitCostRuleWorker
    {
        private static Dictionary<ThingDef, ThingDef> downgrades;

        public override bool Matches(HashSet<string> labelWords, WeaponTraitDef trait)
        {
            return TraitCostUtility.IsNegativeTrait(trait);
        }

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            if (isRemoval)
                return;
            if (downgrades == null)
            {
                downgrades = new Dictionary<ThingDef, ThingDef>
                {
                    { ThingDefOf.ComponentSpacer, ThingDefOf.ComponentIndustrial },
                    { ThingDefOf.Plasteel, ThingDefOf.Steel },
                    { ThingDefOf.ComponentIndustrial, ThingDefOf.Steel },
                    { ThingDefOf.Steel, ThingDefOf.WoodLog },
                };
            }

            var downgraded = new List<ThingDefCountClass>();
            foreach (ThingDefCountClass cost in costs)
            {
                ThingDef mat = downgrades.TryGetValue(cost.thingDef, out ThingDef replacement)
                    ? replacement
                    : cost.thingDef;
                CostRuleHelpers.AddOrMerge(downgraded, mat, cost.count);
            }

            costs.Clear();
            costs.AddRange(downgraded);
        }
    }
}

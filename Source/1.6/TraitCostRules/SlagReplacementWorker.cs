using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Replaces all costs with a single steel slag chunk. Used for crude "heavy
    // scrap" modifications.
    public class SlagReplacementWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.ApplyFlatCost(costs, CostRuleHelpers.SteelSlagChunk, 1);
        }
    }
}

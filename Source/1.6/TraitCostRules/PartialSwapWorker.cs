using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Swaps a fraction of one base material for another by count (1:1). Used
    // for lightweight bow traits that replace wood with birdskin.
    public class PartialSwapWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            if (CostRuleHelpers.Birdskin != null)
                CostRuleHelpers.ApplyPartialSwapByCount(
                    costs, CostRuleHelpers.WoodLog, CostRuleHelpers.Birdskin, 0.4f);
        }
    }
}

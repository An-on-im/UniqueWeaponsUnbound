using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Removes components, then converts half of remaining costs to silver by
    // count. Ornamental work is decorative craftsmanship, not technical
    // engineering.
    public class OrnamentalWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.RemoveMaterials(costs,
                CostRuleHelpers.ComponentIndustrial, CostRuleHelpers.ComponentSpacer);
            CostRuleHelpers.ConvertHalfByCount(costs, CostRuleHelpers.Silver);
        }
    }
}

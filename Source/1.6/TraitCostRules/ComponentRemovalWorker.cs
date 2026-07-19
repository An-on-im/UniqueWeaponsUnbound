using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Removes industrial and spacer components from the cost list. Used for
    // simple modifications (grips, inlays) that are mechanical or cosmetic and
    // don't require complex parts — only raw materials.
    public class ComponentRemovalWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.RemoveMaterials(costs,
                CostRuleHelpers.ComponentIndustrial, CostRuleHelpers.ComponentSpacer);
        }
    }
}

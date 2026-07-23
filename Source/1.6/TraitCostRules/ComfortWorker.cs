using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Converts half of all cost materials into thrumbofur by count, reflecting
    // the luxurious padding used for comfort modifications.
    public class ComfortWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.ConvertHalfByCount(costs, CostRuleHelpers.Thrumbofur);
        }
    }
}

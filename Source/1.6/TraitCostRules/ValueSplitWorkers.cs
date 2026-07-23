using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Base class for workers that split off a fraction of base materials and
    // convert them to a replacement material by market value.
    public abstract class ValueSplitWorker : TraitCostRuleWorker
    {
        protected abstract ThingDef Replacement { get; }
        protected virtual float SplitFraction => 0.7f;

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.ApplyValueSplit(costs, Replacement, SplitFraction);
        }
    }

    // Converts 70% of base materials into industrial components by market value
    // for EMP-related traits. Removes spacer components first to avoid
    // double-counting.
    public class EmpSplitWorker : ValueSplitWorker
    {
        protected override ThingDef Replacement => CostRuleHelpers.ComponentIndustrial;

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.RemoveMaterials(costs, CostRuleHelpers.ComponentSpacer);
            base.Apply(costs, weapon, trait, isRemoval);
        }
    }

    // Converts 70% of base materials into bioferrite by market value for
    // flarestriker traits.
    public class FlareSplitWorker : ValueSplitWorker
    {
        protected override ThingDef Replacement => CostRuleHelpers.Bioferrite;
    }
}

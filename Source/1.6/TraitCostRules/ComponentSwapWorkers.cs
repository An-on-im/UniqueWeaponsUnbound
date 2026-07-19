using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Base class for workers that replace components with a thematic material,
    // falling back to a value-based split of base materials when no components
    // exist.
    public abstract class ComponentSwapOrSplitWorker : TraitCostRuleWorker
    {
        protected abstract ThingDef Replacement { get; }
        protected abstract int ComponentMultiplier { get; }
        protected virtual float SplitFraction => 0.7f;

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            CostRuleHelpers.ApplyComponentSwapOrSplit(
                costs, Replacement, ComponentMultiplier, SplitFraction);
        }
    }

    // Replaces components with herbal medicine (3x count) for toxic/paralytic
    // traits.
    public class ToxSwapWorker : ComponentSwapOrSplitWorker
    {
        protected override ThingDef Replacement => CostRuleHelpers.HerbalMedicine;
        protected override int ComponentMultiplier => 3;
    }

    // Replaces components with chemfuel (10x count) for incendiary/blast
    // traits. Folds spacer components into industrial before swapping so all
    // components are captured in a single pass.
    public class IncendiarySwapWorker : ComponentSwapOrSplitWorker
    {
        protected override ThingDef Replacement => CostRuleHelpers.Chemfuel;
        protected override int ComponentMultiplier => 10;

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            // Fold spacer components into industrial so the base swap catches both
            ThingDefCountClass spacer = costs.Find(c => c.thingDef == CostRuleHelpers.ComponentSpacer);
            if (spacer != null)
            {
                int count = spacer.count;
                costs.Remove(spacer);
                CostRuleHelpers.AddOrMerge(costs, CostRuleHelpers.ComponentIndustrial, count);
            }
            base.Apply(costs, weapon, trait, isRemoval);
        }
    }
}

using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Provides a fallback base cost derived from the weapon's tech level. Runs
    // first in the pipeline so that weapons without a craftable base def still
    // have a reasonable cost. Weapons with recipes will have these costs
    // replaced by BaseCostFromRecipeWorker.
    public class TechLevelCostWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            TechLevel tech = weapon.def.techLevel;

            switch (tech)
            {
                case TechLevel.Neolithic:
                    costs.Add(new ThingDefCountClass(ThingDefOf.WoodLog, 100));
                    break;
                case TechLevel.Medieval:
                    costs.Add(new ThingDefCountClass(ThingDefOf.Steel, 100));
                    break;
                case TechLevel.Industrial:
                    costs.Add(new ThingDefCountClass(ThingDefOf.Steel, 80));
                    costs.Add(new ThingDefCountClass(ThingDefOf.ComponentIndustrial, 6));
                    break;
                default: // Spacer, Ultra, Archotech
                    costs.Add(new ThingDefCountClass(ThingDefOf.Plasteel, 80));
                    costs.Add(new ThingDefCountClass(ThingDefOf.ComponentSpacer, 4));
                    break;
            }
        }
    }
}

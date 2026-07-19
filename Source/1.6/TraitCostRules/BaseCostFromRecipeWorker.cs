using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Replaces costs with the weapon's actual crafting recipe ingredients. Only
    // acts when the weapon has a craftable base def with recipe costs;
    // otherwise leaves the tech-level fallback costs in place.
    public class BaseCostFromRecipeWorker : TraitCostRuleWorker
    {
        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            if (UWU_Mod.Settings is { useRecipeBaseCost: false })
                return;

            ThingDef baseDef = WeaponRegistry.IsUniqueWeapon(weapon.def)
                ? WeaponRegistry.GetBaseVariant(weapon.def)
                : weapon.def;

            // Fall back to the weapon's own def for recipe resolution.
            // Handles base-def-less unique weapons that have their own crafting recipe.
            ThingDef recipeDef = baseDef ?? weapon.def;
            if (recipeDef == null)
                return;

            var recipeCosts = new List<ThingDefCountClass>();

            if (recipeDef.costList != null)
            {
                foreach (ThingDefCountClass entry in recipeDef.costList)
                    recipeCosts.Add(new ThingDefCountClass(entry.thingDef, entry.count));
            }

            if (recipeDef.costStuffCount > 0)
            {
                ThingDef stuff = weapon.Stuff
                    ?? GenStuff.DefaultStuffFor(recipeDef)
                    ?? ThingDefOf.Steel;
                recipeCosts.Add(new ThingDefCountClass(stuff, recipeDef.costStuffCount));
            }

            if (recipeCosts.Count > 0)
            {
                costs.Clear();
                costs.AddRange(recipeCosts);
            }
        }
    }
}

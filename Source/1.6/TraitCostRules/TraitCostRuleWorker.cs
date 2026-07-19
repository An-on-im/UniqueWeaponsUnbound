using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Base class for trait cost rule workers. Subclass this to implement custom
    // cost transformations. The default Matches implementation checks label
    // keywords and weapon categories from the def; override for unconditional
    // rules.
    public abstract class TraitCostRuleWorker
    {
        public TraitCostRuleDef def;

        // Whether this rule applies to the given trait. Default checks keywords
        // (any match unless requireAllKeywords) and optional weapon category
        // filter. Returns true unconditionally when no keywords are defined.
        public virtual bool Matches(HashSet<string> labelWords, WeaponTraitDef trait)
        {
            if (!def.labelKeywords.NullOrEmpty())
            {
                if (def.weaponCategories != null && def.weaponCategories.Count > 0
                    && (trait.weaponCategory == null
                        || !def.weaponCategories.Contains(trait.weaponCategory)))
                    return false;

                return def.requireAllKeywords
                    ? def.labelKeywords.All(k => labelWords.Contains(k))
                    : def.labelKeywords.Any(k => labelWords.Contains(k));
            }

            return true;
        }

        // Applies this rule's cost transformation. Called only when Matches()
        // returns true. isRemoval indicates whether costs are being calculated
        // for trait removal (true) or addition (false). Most workers ignore
        // this distinction.
        public abstract void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval);
    }
}

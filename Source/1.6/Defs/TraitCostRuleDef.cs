using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Defines a trait cost rule that participates in the cost calculation
    // pipeline. Rules are executed in priority order (lower first). Each rule
    // has a worker class that performs the actual cost transformation.
    public class TraitCostRuleDef : Def
    {
        public Type workerClass;
        public int priority;
        public List<string> labelKeywords;
        public bool requireAllKeywords;
        public List<WeaponCategoryDef> weaponCategories;

        [Unsaved(false)]
        private TraitCostRuleWorker workerInt;

        public TraitCostRuleWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (TraitCostRuleWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string err in base.ConfigErrors())
                yield return err;
            if (workerClass == null)
                yield return "workerClass is null";
            else if (!typeof(TraitCostRuleWorker).IsAssignableFrom(workerClass))
                yield return $"workerClass {workerClass} must extend TraitCostRuleWorker";
        }
    }
}

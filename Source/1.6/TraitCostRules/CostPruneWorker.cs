using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Prunes the cost list to at most 3 material types by removing the cheapest
    // entries (by unit market value) until the limit is met. Prevents UI
    // overflow from too many cost icons.
    public class CostPruneWorker : TraitCostRuleWorker
    {
        private const int MaxMaterialTypes = 3;

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            while (costs.Count > MaxMaterialTypes)
            {
                int cheapestIndex = 0;
                float cheapestValue = costs[0].thingDef.BaseMarketValue;
                for (int i = 1; i < costs.Count; i++)
                {
                    float value = costs[i].thingDef.BaseMarketValue;
                    if (value < cheapestValue)
                    {
                        cheapestValue = value;
                        cheapestIndex = i;
                    }
                }
                costs.RemoveAt(cheapestIndex);
            }
        }
    }
}

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Applies the cost fraction (0.5x base recipe) and quality-based
    // multiplier.
    public class QualityMultiplierWorker : TraitCostRuleWorker
    {
        private const float CostFraction = 0.5f;

        private static readonly Dictionary<QualityCategory, float> QualityMultipliers =
            new Dictionary<QualityCategory, float>
            {
                { QualityCategory.Awful, 0.7f },
                { QualityCategory.Poor, 0.85f },
                { QualityCategory.Normal, 1.0f },
                { QualityCategory.Good, 1.25f },
                { QualityCategory.Excellent, 1.5f },
                { QualityCategory.Masterwork, 2.0f },
                { QualityCategory.Legendary, 2.5f },
            };

        public override void Apply(List<ThingDefCountClass> costs, Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            float qualityMult = GetQualityMultiplier(weapon);
            float totalMult = CostFraction * qualityMult;

            foreach (ThingDefCountClass cost in costs)
                cost.count = Mathf.CeilToInt(cost.count * totalMult);
        }

        public static float GetQualityMultiplier(Thing weapon)
        {
            if (!weapon.TryGetQuality(out QualityCategory quality))
                return 1f;

            return QualityMultipliers.TryGetValue(quality, out float mult) ? mult : 1f;
        }
    }
}

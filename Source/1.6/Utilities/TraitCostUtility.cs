using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Orchestrates trait cost calculation by running data-driven rules from
    /// <see cref="TraitCostRuleDef"/> in priority order. Provides the public API
    /// for addition costs, removal costs/refunds, and negative trait detection.
    /// The pipeline supports asymmetric costs: some rules (e.g. NegativeDowngrade)
    /// behave differently for addition vs removal context.
    /// </summary>
    public static class TraitCostUtility
    {
        /// <summary>
        /// Global multiplier applied to all pipeline costs before any other adjustments.
        /// Reads from mod settings; falls back to 1.0 (no change) if settings are not yet loaded.
        /// </summary>
        public static float CostMultiplier => UWU_Mod.Settings?.traitCostMultiplier ?? 1f;

        /// <summary>
        /// Fraction of the trait's cost returned when removing a trait (or paid for
        /// secondary operations like negative trait additions/removals).
        /// Reads from mod settings; falls back to 0.5 if settings are not yet loaded.
        /// </summary>
        public static float RefundRate => UWU_Mod.Settings?.traitRefundRate ?? 0.5f;

        private static List<TraitCostRuleDef> cachedRules;

        /// <summary>
        /// All registered cost rule defs in priority order.
        /// Used by the startup diagnostic to bucket rules by source mod.
        /// </summary>
        public static IReadOnlyList<TraitCostRuleDef> CachedRules => cachedRules;

        /// <summary>
        /// Initializes material caches and builds the sorted rule list from
        /// DefDatabase. Must be called during StaticConstructorOnStartup
        /// (after all defs are loaded). A non-null <paramref name="report"/>
        /// absorbs any fatal exception so the rest of the mod can still
        /// initialize; passing null preserves the throwing contract for
        /// direct callers.
        /// </summary>
        public static void Initialize(InitDiagnostics report = null)
        {
            try
            {
                CostRuleHelpers.Initialize();

                cachedRules = DefDatabase<TraitCostRuleDef>.AllDefs
                    .OrderBy(d => d.priority)
                    .ToList();
            }
            catch (Exception ex)
            {
                if (report == null) throw;
                report.RecordFailure(nameof(TraitCostUtility), ex);
            }
        }

        /// <summary>
        /// Calculates the base resource cost of a trait for addition context.
        /// Runs all matching rules in priority order with isRemoval=false,
        /// then applies <see cref="CostMultiplier"/>.
        /// </summary>
        public static List<ThingDefCountClass> GetTraitCost(Thing weapon, WeaponTraitDef trait)
        {
            List<ThingDefCountClass> costs = RunPipeline(weapon, trait, isRemoval: false);
            ApplyCostMultiplier(costs);
            return costs;
        }

        /// <summary>
        /// Returns true if the trait is "negative" (undesirable), detected by a
        /// MarketValue stat factor below 1.0 or a negative marketValueOffset.
        /// Negative traits have inverted costs: cheaper to add (RefundRate), and
        /// cost RefundRate to remove.
        /// </summary>
        public static bool IsNegativeTrait(WeaponTraitDef trait)
        {
            if (trait.marketValueOffset < 0f)
                return true;
            if (trait.statFactors == null)
                return false;
            for (int i = 0; i < trait.statFactors.Count; i++)
            {
                if (trait.statFactors[i].stat == StatDefOf.MarketValue
                    && trait.statFactors[i].value < 1f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the per-trait cost of ADDING this trait. Applies <see cref="CostMultiplier"/>
        /// to the pipeline output first. For negative traits, the cost is further reduced by
        /// <see cref="RefundRate"/> (nobody pays full price for a downgrade).
        /// Uses the addition pipeline (materials may be downgraded for negative traits).
        /// </summary>
        public static List<ThingDefCountClass> GetAdditionCost(Thing weapon, WeaponTraitDef trait)
        {
            List<ThingDefCountClass> costs = RunPipeline(weapon, trait, isRemoval: false);
            ApplyCostMultiplier(costs);
            if (IsNegativeTrait(trait))
            {
                foreach (ThingDefCountClass c in costs)
                    c.count = Mathf.CeilToInt(c.count * RefundRate);
                costs.RemoveAll(c => c.count <= 0);
            }
            return costs;
        }

        /// <summary>
        /// Returns the per-trait result of REMOVING this trait. Applies <see cref="CostMultiplier"/>
        /// to the pipeline output first, then <see cref="RefundRate"/>.
        /// For positive traits: materials the player receives back (refund).
        /// For negative traits: materials the player must PAY (cost).
        /// Uses the removal pipeline (original-tier materials preserved for negative traits).
        /// Call <see cref="IsNegativeTrait"/> to determine whether the result is a refund or a cost.
        /// </summary>
        public static List<ThingDefCountClass> GetRemovalCost(Thing weapon, WeaponTraitDef trait)
        {
            List<ThingDefCountClass> costs = RunPipeline(weapon, trait, isRemoval: true);
            ApplyCostMultiplier(costs);
            foreach (ThingDefCountClass c in costs)
                c.count = IsNegativeTrait(trait)
                    ? Mathf.CeilToInt(c.count * RefundRate)
                    : Mathf.FloorToInt(c.count * RefundRate);
            costs.RemoveAll(c => c.count <= 0);
            return costs;
        }

        /// <summary>
        /// Calculates the total resource cost across all additions plus any negative
        /// trait removals (which cost resources instead of refunding them).
        /// </summary>
        public static List<ThingDefCountClass> GetTotalCost(
            Thing weapon, IEnumerable<WeaponTraitDef> traitsToAdd,
            IEnumerable<WeaponTraitDef> traitsToRemove = null)
        {
            var totals = new Dictionary<ThingDef, int>();

            foreach (WeaponTraitDef trait in traitsToAdd)
            {
                foreach (ThingDefCountClass cost in GetAdditionCost(weapon, trait))
                {
                    if (totals.ContainsKey(cost.thingDef))
                        totals[cost.thingDef] += cost.count;
                    else
                        totals[cost.thingDef] = cost.count;
                }
            }

            if (traitsToRemove != null)
            {
                foreach (WeaponTraitDef trait in traitsToRemove)
                {
                    if (!IsNegativeTrait(trait))
                        continue;
                    foreach (ThingDefCountClass cost in GetRemovalCost(weapon, trait))
                    {
                        if (totals.ContainsKey(cost.thingDef))
                            totals[cost.thingDef] += cost.count;
                        else
                            totals[cost.thingDef] = cost.count;
                    }
                }
            }

            return totals.Select(kv => new ThingDefCountClass(kv.Key, kv.Value)).ToList();
        }

        /// <summary>
        /// Calculates the total resource refund for removing traits from the given weapon.
        /// Only positive (non-negative) traits produce refunds. Negative trait removals
        /// cost resources instead and are included in <see cref="GetTotalCost"/>.
        /// Aggregates raw costs across positive traits first, then applies
        /// <see cref="CostMultiplier"/> and <see cref="RefundRate"/> once per material
        /// to avoid cumulative rounding loss.
        /// </summary>
        public static List<ThingDefCountClass> GetTotalRefund(
            Thing weapon, IEnumerable<WeaponTraitDef> traits)
        {
            var totals = new Dictionary<ThingDef, int>();
            foreach (WeaponTraitDef trait in traits)
            {
                if (IsNegativeTrait(trait))
                    continue;
                foreach (ThingDefCountClass cost in RunPipeline(weapon, trait, isRemoval: true))
                {
                    if (totals.ContainsKey(cost.thingDef))
                        totals[cost.thingDef] += cost.count;
                    else
                        totals[cost.thingDef] = cost.count;
                }
            }

            var raw = totals.Select(kv => new ThingDefCountClass(kv.Key, kv.Value)).ToList();
            ApplyCostMultiplier(raw);
            foreach (ThingDefCountClass entry in raw)
                entry.count = Mathf.FloorToInt(entry.count * RefundRate);
            raw.RemoveAll(c => c.count <= 0);
            return raw;
        }

        /// <summary>
        /// Scales all costs by <see cref="CostMultiplier"/>, rounding up.
        /// No-op when the multiplier is 1. Removes entries that round to zero.
        /// </summary>
        internal static void ApplyCostMultiplier(List<ThingDefCountClass> costs)
        {
            float multiplier = CostMultiplier;
            if (multiplier == 1f)
                return;
            foreach (ThingDefCountClass c in costs)
                c.count = Mathf.CeilToInt(c.count * multiplier);
            costs.RemoveAll(c => c.count <= 0);
        }

        internal static List<ThingDefCountClass> RunPipeline(
            Thing weapon, WeaponTraitDef trait, bool isRemoval)
        {
            var costs = new List<ThingDefCountClass>();
            HashSet<string> labelWords = CostRuleHelpers.SplitLabelWords(trait.label);

            foreach (TraitCostRuleDef ruleDef in cachedRules)
            {
                try
                {
                    if (ruleDef.Worker.Matches(labelWords, trait))
                        ruleDef.Worker.Apply(costs, weapon, trait, isRemoval);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Skipped cost rule "
                        + ruleDef.SourceForLog() + " for trait "
                        + trait.SourceForLog() + " due to error: " + ex);
                }
            }

            // Finalize: enforce the per-entry invariant (non-null, non-null
            // def, positive count). Null/null-def entries can only appear if
            // a cost-rule worker inserted them (e.g. via a misconfigured
            // material lookup), so log them — they signal a worker bug rather
            // than a normal zero-cost outcome. count <= 0 is the expected
            // "this material rounded down to nothing" case and stays silent.
            int malformedCount = costs.RemoveAll(c => c == null || c.thingDef == null);
            if (malformedCount > 0)
            {
                Log.Warning("[Unique Weapons Unbound] Cost pipeline for trait "
                    + trait.SourceForLog() + " produced " + malformedCount
                    + " malformed entries (null entry or null thingDef); "
                    + "dropping. Indicates a bug in a TraitCostRuleWorker.");
            }
            costs.RemoveAll(c => c.count <= 0);
            return costs;
        }
    }
}

using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Static helper methods and material caches used by trait cost rule
    // workers. Extracted from TraitCostUtility to be accessible to the worker
    // class hierarchy.
    public static class CostRuleHelpers
    {
        // Material lookup caches
        private static Dictionary<string, ThingDef> materialsByLabel;
        private static HashSet<ThingDef> rawResources;

        // Cached ThingDefs for cost rule workers
        public static ThingDef HerbalMedicine { get; private set; }
        public static ThingDef Chemfuel { get; private set; }
        public static ThingDef ComponentIndustrial { get; private set; }
        public static ThingDef ComponentSpacer { get; private set; }
        public static ThingDef WoodLog { get; private set; }
        public static ThingDef Steel { get; private set; }
        public static ThingDef Plasteel { get; private set; }
        public static ThingDef Birdskin { get; private set; }
        public static ThingDef Bioferrite { get; private set; }
        public static ThingDef SteelSlagChunk { get; private set; }
        public static ThingDef Thrumbofur { get; private set; }
        public static ThingDef Silver { get; private set; }

        // Builds the raw resource and material label caches and resolves
        // ThingDefs. Must be called during StaticConstructorOnStartup (after
        // all defs are loaded).
        public static void Initialize()
        {
            materialsByLabel = new Dictionary<string, ThingDef>();
            rawResources = new HashSet<ThingDef>();

            ThingCategoryDef resourcesRaw =
                DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ResourcesRaw");

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                try
                {
                    if (def.IsStuff || IsInCategory(def, resourcesRaw))
                        rawResources.Add(def);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Skipped raw-resource scan for "
                        + def.SourceForLog() + " due to error: " + ex);
                }
            }

            foreach (ThingDef def in rawResources)
            {
                try
                {
                    RegisterMaterialLabels(def);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Skipped material label cache for "
                        + def.SourceForLog() + " due to error: " + ex);
                }
            }

            HerbalMedicine = ThingDefOf.MedicineHerbal;
            Chemfuel = ThingDefOf.Chemfuel;
            ComponentIndustrial = ThingDefOf.ComponentIndustrial;
            ComponentSpacer = ThingDefOf.ComponentSpacer;
            WoodLog = ThingDefOf.WoodLog;
            Steel = ThingDefOf.Steel;
            Plasteel = ThingDefOf.Plasteel;
            Birdskin = DefDatabase<ThingDef>.GetNamedSilentFail("Leather_Bird");
            Bioferrite = DefDatabase<ThingDef>.GetNamedSilentFail("Bioferrite");
            SteelSlagChunk = DefDatabase<ThingDef>.GetNamedSilentFail("ChunkSlagSteel");
            Thrumbofur = DefDatabase<ThingDef>.GetNamedSilentFail("Leather_Thrumbo");
            Silver = ThingDefOf.Silver;
        }

        private static void RegisterMaterialLabels(ThingDef def)
        {
            string label = def.label?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(label) && label.Length >= 3)
                materialsByLabel[label] = def;

            string defName = def.defName?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(defName) && defName.Length >= 3
                && !materialsByLabel.ContainsKey(defName))
                materialsByLabel[defName] = def;
        }

        // Splits a trait label into a word set containing both the full
        // space-delimited words and the hyphen-delimited parts of any
        // hyphenated words. E.g. "crypto-coated rails" → {"crypto-coated",
        // "rails", "crypto", "coated"}.
        public static HashSet<string> SplitLabelWords(string label)
        {
            var words = new HashSet<string>();
            if (string.IsNullOrEmpty(label))
                return words;

            foreach (string word in label.ToLowerInvariant().Split(' '))
            {
                words.Add(word);
                if (word.Contains("-"))
                {
                    foreach (string part in word.Split('-'))
                    {
                        if (part.Length > 0)
                            words.Add(part);
                    }
                }
            }

            return words;
        }

        // If components exist in the cost list, replace them with multiplier *
        // count of the replacement material. Otherwise, split off a fraction of
        // wood/steel/plasteel and convert by market value.
        public static void ApplyComponentSwapOrSplit(
            List<ThingDefCountClass> costs, ThingDef replacement, int componentMultiplier, float splitFraction)
        {
            if (replacement == null)
                return;

            ThingDefCountClass compEntry = costs.Find(c => c.thingDef == ComponentIndustrial);

            if (compEntry != null)
            {
                int replacementCount = compEntry.count * componentMultiplier;
                costs.Remove(compEntry);
                AddOrMerge(costs, replacement, replacementCount);
            }
            else
            {
                float splitValue = SplitBaseMaterials(costs, splitFraction);
                if (splitValue > 0f && replacement.BaseMarketValue > 0f)
                    AddOrMerge(costs, replacement, Mathf.CeilToInt(splitValue / replacement.BaseMarketValue));
            }
        }

        // Split off a fraction of wood/steel/plasteel and convert to the
        // replacement material by market value.
        public static void ApplyValueSplit(
            List<ThingDefCountClass> costs, ThingDef replacement, float splitFraction)
        {
            if (replacement == null)
                return;

            float splitValue = SplitBaseMaterials(costs, splitFraction);
            if (splitValue > 0f && replacement.BaseMarketValue > 0f)
                AddOrMerge(costs, replacement, Mathf.CeilToInt(splitValue / replacement.BaseMarketValue));
        }

        // Swap a fraction of source material count directly to the replacement
        // material (1:1 by count).
        public static void ApplyPartialSwapByCount(
            List<ThingDefCountClass> costs, ThingDef source, ThingDef replacement, float fraction)
        {
            ThingDefCountClass sourceEntry = costs.Find(c => c.thingDef == source);
            if (sourceEntry == null || sourceEntry.count <= 0)
                return;

            int swapAmount = Mathf.FloorToInt(sourceEntry.count * fraction);
            if (swapAmount <= 0)
                return;

            sourceEntry.count -= swapAmount;
            AddOrMerge(costs, replacement, swapAmount);
        }

        // Convert all non-spacer-component costs into spacer components by
        // market value (rounded up).
        public static void ApplyConvertAllToSpacer(List<ThingDefCountClass> costs)
        {
            if (ComponentSpacer == null || ComponentSpacer.BaseMarketValue <= 0f)
                return;

            float totalValue = 0f;
            int existingSpacerCount = 0;

            for (int i = costs.Count - 1; i >= 0; i--)
            {
                if (costs[i].thingDef == ComponentSpacer)
                {
                    existingSpacerCount += costs[i].count;
                }
                else
                {
                    totalValue += costs[i].count * costs[i].thingDef.BaseMarketValue;
                }
                costs.RemoveAt(i);
            }

            int totalCount = existingSpacerCount + Mathf.CeilToInt(totalValue / ComponentSpacer.BaseMarketValue);
            if (totalCount > 0)
                costs.Add(new ThingDefCountClass(ComponentSpacer, totalCount));
        }

        // Replaces all costs with a single flat entry.
        public static void ApplyFlatCost(List<ThingDefCountClass> costs, ThingDef material, int count)
        {
            if (material == null)
                return;

            costs.Clear();
            costs.Add(new ThingDefCountClass(material, count));
        }

        // Multiplies all cost counts by the given factor (rounded up).
        public static void ApplyCostMultiplier(List<ThingDefCountClass> costs, float multiplier)
        {
            foreach (ThingDefCountClass cost in costs)
                cost.count = Mathf.CeilToInt(cost.count * multiplier);
        }

        // Removes a fraction of wood, steel, and plasteel from the cost list
        // and returns the total market value of what was removed.
        public static float SplitBaseMaterials(List<ThingDefCountClass> costs, float fraction)
        {
            float splitValue = 0f;

            foreach (ThingDefCountClass cost in costs)
            {
                if (cost.thingDef != WoodLog && cost.thingDef != Steel && cost.thingDef != Plasteel)
                    continue;

                int splitAmount = Mathf.FloorToInt(cost.count * fraction);
                if (splitAmount <= 0)
                    continue;

                splitValue += splitAmount * cost.thingDef.BaseMarketValue;
                cost.count -= splitAmount;
            }

            return splitValue;
        }

        // Adds count to an existing entry for the given ThingDef, or creates a
        // new entry.
        public static void AddOrMerge(List<ThingDefCountClass> costs, ThingDef def, int count)
        {
            ThingDefCountClass existing = costs.Find(c => c.thingDef == def);
            if (existing != null)
                existing.count += count;
            else
                costs.Add(new ThingDefCountClass(def, count));
        }

        // Removes all entries matching the given ThingDefs from the cost list.
        public static void RemoveMaterials(List<ThingDefCountClass> costs, params ThingDef[] materials)
        {
            costs.RemoveAll(c =>
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    if (c.thingDef == materials[i])
                        return true;
                }
                return false;
            });
        }

        // Converts half (by count, floored) of every cost entry into the
        // replacement material.
        public static void ConvertHalfByCount(List<ThingDefCountClass> costs, ThingDef replacement)
        {
            if (replacement == null)
                return;

            int totalReplacement = 0;
            foreach (ThingDefCountClass cost in costs)
            {
                int half = Mathf.FloorToInt(cost.count * 0.5f);
                if (half <= 0)
                    continue;
                cost.count -= half;
                totalReplacement += half;
            }

            if (totalReplacement > 0)
                AddOrMerge(costs, replacement, totalReplacement);
        }

        // Returns the material override ThingDef if the trait label contains a
        // known raw resource name (e.g., "gold inlay" matches Gold). Returns
        // null otherwise.
        public static ThingDef GetMaterialOverride(WeaponTraitDef trait)
        {
            if (materialsByLabel == null || materialsByLabel.Count == 0)
                return null;

            ThingDef match = TryMatchWords(trait.label);
            if (match != null)
                return match;

            return TryMatchWords(SplitPascalCase(trait.defName));
        }

        // Sums all raw resource costs and replaces them with the override
        // material. Non-raw costs (e.g. components) pass through unchanged.
        public static void ApplyMaterialOverride(
            List<ThingDefCountClass> costs, ThingDef overrideMaterial)
        {
            int rawTotal = 0;
            var result = new List<ThingDefCountClass>();

            foreach (ThingDefCountClass cost in costs)
            {
                if (rawResources.Contains(cost.thingDef))
                    rawTotal += cost.count;
                else
                    result.Add(cost);
            }

            if (rawTotal > 0)
                result.Insert(0, new ThingDefCountClass(overrideMaterial, rawTotal));

            costs.Clear();
            costs.AddRange(result);
        }

        // Returns true if the given ThingDef is a raw resource (stuff or in
        // ResourcesRaw category).
        public static bool IsRawResource(ThingDef def)
        {
            return rawResources != null && rawResources.Contains(def);
        }

        private static ThingDef TryMatchWords(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            ThingDef bestMatch = null;
            int bestLength = 0;

            foreach (string word in text.ToLowerInvariant().Split(' '))
            {
                if (word.Length < 3)
                    continue;

                if (materialsByLabel.TryGetValue(word, out ThingDef mat) && word.Length > bestLength)
                {
                    bestMatch = mat;
                    bestLength = word.Length;
                }
            }

            return bestMatch;
        }

        private static string SplitPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = new System.Text.StringBuilder(input.Length + 4);
            for (int i = 0; i < input.Length; i++)
            {
                if (i > 0 && char.IsUpper(input[i]))
                    result.Append(' ');
                result.Append(input[i]);
            }
            return result.ToString();
        }

        private static bool IsInCategory(ThingDef def, ThingCategoryDef targetCategory)
        {
            if (targetCategory == null || def.thingCategories == null)
                return false;

            foreach (ThingCategoryDef cat in def.thingCategories)
            {
                ThingCategoryDef current = cat;
                while (current != null)
                {
                    if (current == targetCategory)
                        return true;
                    current = current.parent;
                }
            }

            return false;
        }
    }
}

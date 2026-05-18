using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Workbench tier classification (smithy / machining / fabrication),
    /// VEF recipe-inheritance expansion, and runtime workbench search
    /// for weapon customization.
    /// </summary>
    public static class WorkbenchUtility
    {
        private static HashSet<ThingDef> smithyDefs;
        private static HashSet<ThingDef> machiningDefs;
        private static HashSet<ThingDef> fabricationDefs;
        private static HashSet<ThingDef> weaponWorkbenchDefs;
        private static string smithyLabel;
        private static string machiningLabel;
        private static string fabricationLabel;

        /// <summary>
        /// Initializes workbench tier sets and the weapon-workbench registry.
        /// Must be called during StaticConstructorOnStartup (after all defs
        /// are loaded). A non-null <paramref name="report"/> absorbs any fatal
        /// exception so the rest of the mod can still initialize; passing null
        /// preserves the throwing contract for direct callers.
        /// </summary>
        public static void Initialize(InitDiagnostics report = null)
        {
            try
            {
                // Initialize workbench tier sets from vanilla anchors
                smithyDefs = ResolveDefSet("FueledSmithy", "ElectricSmithy");
                machiningDefs = ResolveDefSet("TableMachining");
                fabricationDefs = ResolveDefSet("FabricationBench");

                // Resolve display labels from vanilla anchors only (before expanding with modded benches)
                smithyLabel = ResolveWorkbenchLabel(smithyDefs);
                machiningLabel = ResolveWorkbenchLabel(machiningDefs);
                fabricationLabel = ResolveWorkbenchLabel(fabricationDefs);

                // Expand tier sets with benches that inherit recipes from vanilla anchors via VEF
                ExpandTiersFromVEF();

                // Build the set of all workbench defs that have at least one weapon recipe
                InitializeWeaponWorkbenches();
            }
            catch (Exception ex)
            {
                if (report == null) throw;
                report.RecordFailure(nameof(WorkbenchUtility), ex);
            }
        }

        /// <summary>
        /// Result of searching for a valid workbench to customize a weapon at.
        /// Either contains a workbench or the highest-priority rejection reason.
        /// </summary>
        public struct WorkbenchSearchResult
        {
            public Building_WorkTable Workbench;
            public AcceptanceReport BestRejection;
            public bool Found => Workbench != null;
        }

        /// <summary>
        /// Finds the closest valid colonist workbench for customizing the specified weapon.
        /// Pawn-specific overload: checks reachability via the pawn's pathfinder and
        /// forbidden status relative to the pawn.
        /// </summary>
        public static WorkbenchSearchResult FindBestWorkbench(
            Pawn pawn, ThingDef baseDef, ThingDef uniqueDef, TechLevel weaponTechLevel,
            IntVec3 distanceOrigin)
        {
            return FindBestWorkbenchCore(pawn.Map, baseDef, uniqueDef, weaponTechLevel,
                distanceOrigin, pawn, workbench =>
                {
                    if (!pawn.CanReach(workbench, PathEndMode.InteractionCell, Danger.Deadly))
                        return "NoPath".Translate();
                    if (workbench.IsForbidden(pawn))
                        return "ForbiddenLower".Translate();
                    return true;
                });
        }

        /// <summary>
        /// Finds the closest valid colonist workbench for customizing the specified weapon.
        /// Pawn-independent overload: checks generic reachability from a map position and
        /// forbidden status relative to the player faction. Used for gizmo enabled/disabled
        /// state where no specific pawn is known yet.
        /// </summary>
        public static WorkbenchSearchResult FindBestWorkbench(
            Map map, ThingDef baseDef, ThingDef uniqueDef, TechLevel weaponTechLevel,
            IntVec3 distanceOrigin)
        {
            return FindBestWorkbenchCore(map, baseDef, uniqueDef, weaponTechLevel,
                distanceOrigin, null, workbench =>
                {
                    if (!map.reachability.CanReach(distanceOrigin, workbench,
                            PathEndMode.InteractionCell,
                            TraverseParms.For(TraverseMode.PassDoors)))
                        return "NoPath".Translate();
                    if (workbench.IsForbidden(Faction.OfPlayer))
                        return "ForbiddenLower".Translate();
                    return true;
                });
        }

        /// <summary>
        /// Common core for workbench search. Iterates colonist workbenches, applies tier
        /// and operational checks, then delegates reachability/forbidden checks to the
        /// caller-provided predicate. Returns the closest valid workbench or the
        /// highest-priority rejection reason.
        /// </summary>
        private static WorkbenchSearchResult FindBestWorkbenchCore(
            Map map, ThingDef baseDef, ThingDef uniqueDef, TechLevel weaponTechLevel,
            IntVec3 distanceOrigin, Pawn pawn,
            Func<Building_WorkTable, AcceptanceReport> accessCheck)
        {
            // Track two tiers: prefer unreserved benches, fall back to reserved.
            // This avoids interrupting in-progress work when a free bench is available,
            // while still allowing it when all valid benches are occupied.
            Building_WorkTable bestFree = null;
            float bestFreeDistSq = float.MaxValue;
            Building_WorkTable bestReserved = null;
            float bestReservedDistSq = float.MaxValue;
            int bestRejectionPriority = -1;
            AcceptanceReport bestRejection = false;

            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (!(building is Building_WorkTable workbench))
                    continue;
                if (!weaponWorkbenchDefs.Contains(workbench.def))
                    continue;

                // Tier check (priority 4)
                AcceptanceReport tierReport = CanCustomizeAtWorkbench(
                    baseDef, uniqueDef, weaponTechLevel, workbench);
                if (!tierReport.Accepted)
                {
                    if (bestRejectionPriority < 4)
                    {
                        bestRejectionPriority = 4;
                        bestRejection = tierReport;
                    }
                    continue;
                }

                // Operational check (priority 3)
                AcceptanceReport opReport = GetWorkbenchOperationalReport(workbench);
                if (!opReport.Accepted)
                {
                    if (bestRejectionPriority < 3)
                    {
                        bestRejectionPriority = 3;
                        bestRejection = opReport;
                    }
                    continue;
                }

                // Caller-provided access check (reachability + forbidden)
                AcceptanceReport accessReport = accessCheck(workbench);
                if (!accessReport.Accepted)
                {
                    // Determine priority from the rejection reason
                    int priority = accessReport.Reason == "ForbiddenLower".Translate() ? 1 : 2;
                    if (bestRejectionPriority < priority)
                    {
                        bestRejectionPriority = priority;
                        bestRejection = accessReport;
                    }
                    continue;
                }

                // Valid candidate — sort into free vs reserved, track closest in each
                float distSq = (distanceOrigin - workbench.Position).LengthHorizontalSquared;
                bool reservedByOther = pawn != null
                    ? map.reservationManager.IsReserved(workbench)
                        && !map.reservationManager.ReservedBy(workbench, pawn)
                    : map.reservationManager.IsReserved(workbench);
                if (reservedByOther)
                {
                    if (distSq < bestReservedDistSq)
                    {
                        bestReservedDistSq = distSq;
                        bestReserved = workbench;
                    }
                }
                else
                {
                    if (distSq < bestFreeDistSq)
                    {
                        bestFreeDistSq = distSq;
                        bestFree = workbench;
                    }
                }
            }

            var result = new WorkbenchSearchResult();
            Building_WorkTable bestWorkbench = bestFree ?? bestReserved;
            if (bestWorkbench != null)
            {
                result.Workbench = bestWorkbench;
            }
            else
            {
                result.BestRejection = bestRejectionPriority >= 0
                    ? bestRejection
                    : "UWU_NoSuitableWorkbench".Translate();
            }
            return result;
        }

        /// <summary>
        /// Whether the workbench has at least one recipe that produces a weapon,
        /// making it eligible to show the customization float menu option.
        /// </summary>
        public static bool IsCustomizationWorkbench(Building_WorkTable workbench)
        {
            return weaponWorkbenchDefs.Contains(workbench.def);
        }

        /// <summary>
        /// Whether the given workbench supports customizing the specified weapon.
        /// First checks if the bench has a recipe for the weapon (base or unique def).
        /// Falls back to tech-level tier matching (vanilla anchors + VEF inheritance).
        /// Returns AcceptanceReport with the required workbench name when the tier is too low.
        /// </summary>
        public static AcceptanceReport CanCustomizeAtWorkbench(
            ThingDef baseDef, ThingDef uniqueDef, TechLevel weaponTechLevel,
            Building_WorkTable workbench)
        {
            // Setting disabled — any weapon-crafting workbench is sufficient
            if (!UWU_Mod.Settings.requireAppropriateWorkbench)
                return true;

            // Layer 2: Direct recipe match — if this bench can craft this weapon, allow customization
            if (WorkbenchHasRecipeFor(workbench.def, baseDef, uniqueDef))
                return true;

            // Layer 3: Tech-level tier fallback.
            //
            // Uses a tier-ceiling fallthrough that mirrors GetRequiredResearch, so weapons
            // tagged with Animal or Undefined fall up to the smithy tier instead of being
            // silently rejected.
            ThingDef benchDef = workbench.def;
            bool isMachiningOrHigher = machiningDefs.Contains(benchDef) || fabricationDefs.Contains(benchDef);
            bool isFabrication = fabricationDefs.Contains(benchDef);

            if (weaponTechLevel >= TechLevel.Spacer)
            {
                if (isFabrication)
                    return true;
                return "UWU_RequiresWorkbench".Translate(fabricationLabel);
            }
            if (weaponTechLevel >= TechLevel.Industrial)
            {
                if (isMachiningOrHigher)
                    return true;
                return "UWU_RequiresWorkbench".Translate(machiningLabel);
            }
            return true;
        }

        /// <summary>
        /// Whether the workbench is operational (powered and/or fueled as required).
        /// Returns AcceptanceReport with a rejection reason if not operational.
        /// </summary>
        public static AcceptanceReport GetWorkbenchOperationalReport(Building_WorkTable workbench)
        {
            CompPowerTrader power = workbench.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
                return "NoPower".Translate();

            CompRefuelable fuel = workbench.TryGetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel)
                return "NoFuel".Translate();

            return true;
        }

        /// <summary>
        /// Resolves an array of defNames into a set of ThingDefs, silently skipping any that don't exist.
        /// </summary>
        private static HashSet<ThingDef> ResolveDefSet(params string[] defNames)
        {
            var set = new HashSet<ThingDef>();
            foreach (string name in defNames)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def != null)
                    set.Add(def);
            }
            return set;
        }

        /// <summary>
        /// Resolves a display label for a set of workbench defNames by finding the
        /// common suffix of their labels. For a single def, returns its label directly.
        /// This handles cases like "fueled smithy" / "electric smithy" → "smithy".
        /// </summary>
        private static string ResolveWorkbenchLabel(HashSet<ThingDef> defs)
        {
            List<string> labels = new List<string>();
            foreach (ThingDef def in defs)
            {
                labels.Add(def.label);
            }

            if (labels.Count == 0)
                return "?";
            if (labels.Count == 1)
                return labels[0];

            // Find the longest common suffix across all labels.
            string reference = labels[0];
            int commonLength = reference.Length;
            for (int i = 1; i < labels.Count; i++)
            {
                string other = labels[i];
                int matchLen = 0;
                int ri = reference.Length - 1;
                int oi = other.Length - 1;
                while (ri >= 0 && oi >= 0 && reference[ri] == other[oi])
                {
                    matchLen++;
                    ri--;
                    oi--;
                }
                commonLength = Math.Min(commonLength, matchLen);
            }

            if (commonLength > 0)
            {
                string suffix = reference.Substring(reference.Length - commonLength).TrimStart();
                if (suffix.Length > 0)
                    return suffix;
            }

            return labels[0];
        }

        /// <summary>
        /// Expands workbench tier sets by walking VEF's RecipeInheritanceExtension.
        /// Benches that inherit recipes from a vanilla anchor are classified at the
        /// highest tier of their inheritance sources.
        /// </summary>
        private static void ExpandTiersFromVEF()
        {
            Type extensionType = GenTypes.GetTypeInAnyAssembly("VEF.Buildings.RecipeInheritanceExtension");
            if (extensionType == null)
                return;

            FieldInfo field = extensionType.GetField("inheritRecipesFrom",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                return;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                try
                {
                    ClassifyVEFInheritedDef(def, extensionType, field);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Skipped VEF tier classification for "
                        + def.SourceForLog() + " due to error: " + ex);
                }
            }
        }

        private static void ClassifyVEFInheritedDef(ThingDef def, Type extensionType, FieldInfo field)
        {
            if (def.modExtensions == null)
                return;
            if (smithyDefs.Contains(def) || machiningDefs.Contains(def) || fabricationDefs.Contains(def))
                return;

            foreach (DefModExtension ext in def.modExtensions)
            {
                if (!extensionType.IsInstanceOfType(ext))
                    continue;

                if (!(field.GetValue(ext) is List<ThingDef> inheritFrom))
                    continue;

                // Classify at highest inherited tier
                HashSet<ThingDef> bestTier = null;
                foreach (ThingDef source in inheritFrom)
                {
                    if (fabricationDefs.Contains(source))
                    {
                        bestTier = fabricationDefs;
                        break;
                    }
                    if (machiningDefs.Contains(source))
                        bestTier = machiningDefs;
                    else if (smithyDefs.Contains(source) && bestTier == null)
                        bestTier = smithyDefs;
                }

                if (bestTier != null)
                    bestTier.Add(def);
                return;
            }
        }

        /// <summary>
        /// Builds the set of all Building_WorkTable defs that have at least one recipe
        /// producing a weapon. Used as the visibility gate for the customization float menu.
        /// </summary>
        private static void InitializeWeaponWorkbenches()
        {
            weaponWorkbenchDefs = new HashSet<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                try
                {
                    if (DefHasWeaponRecipe(def))
                        weaponWorkbenchDefs.Add(def);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Skipped workbench scan for "
                        + def.SourceForLog() + " due to error: " + ex);
                }
            }

            // Include tier-classified benches regardless of whether their inherited
            // recipes are visible yet (VEF may not have processed them due to load order)
            weaponWorkbenchDefs.UnionWith(smithyDefs);
            weaponWorkbenchDefs.UnionWith(machiningDefs);
            weaponWorkbenchDefs.UnionWith(fabricationDefs);
        }

        private static bool DefHasWeaponRecipe(ThingDef def)
        {
            if (!typeof(Building_WorkTable).IsAssignableFrom(def.thingClass))
                return false;

            List<RecipeDef> recipes = def.AllRecipes;
            if (recipes == null)
                return false;

            foreach (RecipeDef recipe in recipes)
            {
                if (recipe.ProducedThingDef != null && recipe.ProducedThingDef.IsWeapon)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether the workbench def has a recipe that produces the given base or unique weapon def.
        /// </summary>
        private static bool WorkbenchHasRecipeFor(ThingDef benchDef, ThingDef baseDef, ThingDef uniqueDef)
        {
            List<RecipeDef> recipes = benchDef.AllRecipes;
            if (recipes == null)
                return false;

            foreach (RecipeDef recipe in recipes)
            {
                ThingDef produced = recipe.ProducedThingDef;
                if (produced != null && (produced == baseDef || produced == uniqueDef))
                    return true;
            }
            return false;
        }
    }
}

using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Stateless game-rule predicates for determining whether weapons are
    /// customizable and what research is required.
    /// </summary>
    public static class CustomizationRules
    {
        /// <summary>
        /// Whether this weapon has a customization path and the player has unlocked
        /// the required customization research. Does not check craftability (recipe
        /// research) — call <see cref="GetCraftabilityReport"/> separately so callers
        /// can insert context-dependent checks (e.g. workbench tier) in between.
        /// Returns AcceptanceReport with a rejection reason if not customizable.
        /// Returns false with no reason when the option should be hidden entirely.
        /// </summary>
        public static AcceptanceReport IsCustomizable(Thing weapon)
        {
            ThingDef def = weapon.def;

            if (WeaponRegistry.IsUniqueWeapon(def))
            {
                // Unique weapons are always in the customization system
                // regardless of whether a base def exists.
            }
            else
            {
                if (WeaponRegistry.GetUniqueVariant(def) == null)
                    return HiddenUnlessDev("UWU_DevNoUniqueVariant".Translate());

                // When def conversion is disabled, only already-unique weapons
                // can enter the customization system.
                if (!UWU_Mod.Settings.allowDefConversion)
                    return HiddenUnlessDev("UWU_DevDefConversionDisabled".Translate());
            }

            // Tech-level ceiling applies regardless of requireCustomizationResearch:
            // the Ultra/Archotech setting toggles are about whether those tiers participate
            // in the customization system at all, not about gating the research projects.
            ResearchProjectDef requiredResearch = GetRequiredResearch(def.techLevel);
            if (requiredResearch == null)
                return HiddenUnlessDev("UWU_DevTechLevelBeyondComprehension".Translate(def.techLevel.ToStringHuman()));

            if (UWU_Mod.Settings.requireCustomizationResearch)
            {
                // Don't surface customization UI until the player has completed UniqueSmithing,
                // so we don't clutter menus for uninterested players. Bypassed in dev mode so the
                // per-weapon research blocker is shown instead.
                if (!Prefs.DevMode && !UWU_ResearchDefOf.UniqueSmithing.IsFinished)
                    return false;

                if (!requiredResearch.IsFinished)
                    return "UWU_RequiresResearch".Translate(requiredResearch.label);
            }

            QualityCategory minQuality = UWU_Mod.Settings.minimumQuality;
            if (minQuality > QualityCategory.Awful
                && weapon.TryGetQuality(out QualityCategory quality)
                && quality < minQuality)
            {
                return "UWU_RequiresMinimumQuality".Translate(minQuality.GetLabel());
            }

            return true;
        }

        /// <summary>
        /// Whether the base weapon's crafting prerequisites are met.
        /// Returns AcceptanceReport with the blocking research name, or false
        /// with no reason for uncraftable weapons without the mod setting.
        /// </summary>
        public static AcceptanceReport GetCraftabilityReport(ThingDef baseDef, ThingDef uniqueDef)
        {
            RecipeMakerProperties recipeMaker = baseDef?.recipeMaker ?? uniqueDef?.recipeMaker;
            if (recipeMaker == null)
                return UWU_Mod.Settings.allowUncraftableCustomization;

            if (UWU_Mod.Settings.requireRecipeResearch)
            {
                ResearchProjectDef recipeResearch = recipeMaker.researchPrerequisite;
                if (recipeResearch != null && !recipeResearch.IsFinished)
                    return "UWU_RequiresResearch".Translate(recipeResearch.label);
            }

            return true;
        }

        /// <summary>
        /// Returns the required research project for customizing weapons of the given tech level,
        /// or null when the tech level is above the configured customization ceiling
        /// (i.e. Ultra/Archotech with their mod settings disabled).
        ///
        /// Uses tier fallthroughs at both ends: weapons tagged Animal or Undefined fall up to
        /// UniqueSmithing, and the fabrication tier extends up to whichever high-end tier the
        /// player has enabled. This makes the gate robust against modded weapons with unusual
        /// tech levels.
        /// </summary>
        public static ResearchProjectDef GetRequiredResearch(TechLevel techLevel)
        {
            if (techLevel > GetCustomizationCeiling())
                return null;
            if (techLevel >= TechLevel.Spacer)
                return UWU_ResearchDefOf.UniqueFabrication;
            if (techLevel >= TechLevel.Industrial)
                return UWU_ResearchDefOf.UniqueMachining;
            return UWU_ResearchDefOf.UniqueSmithing;
        }

        /// <summary>
        /// The highest tech level the player has opted into customizing. Anything above
        /// this is "beyond comprehension" and falls out of the customization system.
        /// Archotech implies Ultra, since they share the same research gate.
        /// </summary>
        private static TechLevel GetCustomizationCeiling()
        {
            if (UWU_Mod.Settings.allowArchotechCustomization)
                return TechLevel.Archotech;
            if (UWU_Mod.Settings.allowUltratechCustomization)
                return TechLevel.Ultra;
            return TechLevel.Spacer;
        }

        /// <summary>
        /// Whether the player has completed the required research for the given tech level.
        /// </summary>
        public static bool HasRequiredResearch(TechLevel techLevel)
        {
            ResearchProjectDef required = GetRequiredResearch(techLevel);
            return required != null && required.IsFinished;
        }

        /// <summary>
        /// Rejection report for paths that are normally hidden (silent <c>false</c>).
        /// In dev mode, surfaces the reason so the option/gizmo renders as visible-but-disabled,
        /// letting modders diagnose why a weapon isn't customizable without exporting logs.
        /// </summary>
        private static AcceptanceReport HiddenUnlessDev(string devReason)
        {
            if (!Prefs.DevMode)
                return false;
            return devReason;
        }

        /// <summary>
        /// Returns the weapon's tech level if it participates in the customization system.
        /// Returns TechLevel.Undefined if the weapon has no customization path.
        /// </summary>
        public static TechLevel GetWeaponTechLevel(Thing weapon)
        {
            ThingDef def = weapon.def;

            if (WeaponRegistry.IsUniqueWeapon(def))
                return def.techLevel;

            if (WeaponRegistry.GetUniqueVariant(def) != null)
                return def.techLevel;

            return TechLevel.Undefined;
        }
    }
}

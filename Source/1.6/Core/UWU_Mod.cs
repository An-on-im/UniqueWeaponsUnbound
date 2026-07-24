using RimWorld;
using UnityEngine;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;

namespace UniqueWeaponsUnbound
{
    public class UWU_Mod : Mod
    {
        public static UWU_Settings Settings { get; private set; }

        private Vector2 settingsScroll;
        private float settingsHeight;

        public UWU_Mod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<UWU_Settings>();
        }

        public override string SettingsCategory() => "UWU_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            float buttonHeight = 30f;
            float buttonGap = 10f;
            Rect viewRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - buttonHeight - buttonGap);
            Rect buttonRect = new Rect(inRect.x, inRect.yMax - buttonHeight, 200f, buttonHeight);

            float innerWidth = viewRect.width - 16f;
            Rect innerRect = new Rect(0f, 0f, innerWidth, Mathf.Max(settingsHeight, viewRect.height));
            Widgets.BeginScrollView(viewRect, ref settingsScroll, innerRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, innerWidth - 8f, 99999f));
            GameFont prev = Text.Font;

            listing.Gap();

            Text.Font = GameFont.Medium;
            listing.Label("UWU_SettingsProgression".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12.0f);

            listing.CheckboxLabeled(
                "UWU_RestrictTraitsToDiscovered".Translate(),
                ref Settings.restrictTraitsToDiscovered,
                "UWU_RestrictTraitsToDiscoveredDesc".Translate());

            listing.Gap(18.0f);

            Text.Font = GameFont.Medium;
            listing.Label("UWU_SettingsTraitCosts".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12.0f);

            listing.CheckboxLabeled(
                "UWU_UseRecipeBaseCost".Translate(),
                ref Settings.useRecipeBaseCost,
                "UWU_UseRecipeBaseCostDesc".Translate());

            listing.Gap();

            string costPct = (Settings.traitCostMultiplier * 100f).ToString("F0");
            string costLabel = "UWU_TraitCostMultiplier".Translate(costPct);
            if (Settings.traitCostMultiplier == 1f)
                costLabel += "UWU_DefaultSuffix".Translate();
            listing.Label(costLabel);
            Settings.traitCostMultiplier = listing.Slider(Settings.traitCostMultiplier, 0f, 5f);
            Settings.traitCostMultiplier = Mathf.Round(Settings.traitCostMultiplier * 20f) / 20f;

            bool costsFree = Settings.traitCostMultiplier == 0f;
            string refundPct = (Settings.traitRefundRate * 100f).ToString("F0");
            string refundLabel = "UWU_TraitRefundRate".Translate(refundPct);
            if (Settings.traitRefundRate == 0.5f)
                refundLabel += "UWU_DefaultSuffix".Translate();
            if (costsFree)
            {
                Color prevColor = GUI.color;
                GUI.color = Color.gray;
                listing.Label(refundLabel);
                Rect sliderRect = listing.GetRect(22f);
                Widgets.HorizontalSlider(sliderRect, Settings.traitRefundRate, 0f, 1f);
                TooltipHandler.TipRegion(sliderRect,
                    "UWU_RefundRateNoEffect".Translate());
                GUI.color = prevColor;
            }
            else
            {
                listing.Label(refundLabel);
                Settings.traitRefundRate = listing.Slider(Settings.traitRefundRate, 0f, 1f);
                Settings.traitRefundRate = Mathf.Round(Settings.traitRefundRate * 20f) / 20f;
            }

            listing.Gap(18.0f);

            Text.Font = GameFont.Medium;
            listing.Label("UWU_SettingsPrerequisites".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(6f);

            string qualityLabel = "UWU_MinimumQuality".Translate(Settings.minimumQuality.GetLabel());
            if (Settings.minimumQuality == QualityCategory.Awful)
                qualityLabel += "UWU_DefaultSuffix".Translate();
            else if (Settings.minimumQuality == QualityCategory.Normal)
                qualityLabel += "UWU_RecommendedSuffix".Translate();
            listing.Label(qualityLabel, tooltip: "UWU_MinimumQualityDesc".Translate());
            float qualityValue = (int)Settings.minimumQuality;
            qualityValue = listing.Slider(qualityValue, 0f, (int)QualityCategory.Legendary);
            Settings.minimumQuality = (QualityCategory)Mathf.RoundToInt(qualityValue);

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_AllowDefConversion".Translate(),
                ref Settings.allowDefConversion,
                "UWU_AllowDefConversionDesc".Translate());

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_RequireCustomizationResearch".Translate(),
                ref Settings.requireCustomizationResearch,
                "UWU_RequireCustomizationResearchDesc".Translate(
                    UWU_ResearchDefOf.UniqueSmithing.label,
                    UWU_ResearchDefOf.UniqueMachining.label,
                    UWU_ResearchDefOf.UniqueFabrication.label));

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_RequireRecipeResearch".Translate(),
                ref Settings.requireRecipeResearch,
                "UWU_RequireRecipeResearchDesc".Translate(UWU_WeaponsDefOf.Gun_ChargeRifle.label, UWU_ResearchDefOf.ChargedShot.label));

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_RequireWorkbench".Translate(),
                ref Settings.requireAppropriateWorkbench,
                "UWU_RequireWorkbenchDesc".Translate());

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_AllowUncraftable".Translate(),
                ref Settings.allowUncraftableCustomization,
                "UWU_AllowUncraftableDesc".Translate());

            listing.Gap();

            if (Settings.allowArchotechCustomization)
            {
                // Visually forced on — Archotech implies Ultratech at runtime
                // (see CustomizationRules.GetRequiredResearch). Stored setting is
                // left untouched so toggling Archotech off restores prior intent.
                Color prevColor = GUI.color;
                Color prevContent = GUI.contentColor;
                GUI.color = new Color(0.4f, 0.4f, 0.4f);
                GUI.contentColor = new Color(0.5f, 0.5f, 0.5f);
                bool forcedOn = true;
                listing.CheckboxLabeled(
                    "UWU_AllowUltratech".Translate(),
                    ref forcedOn,
                    "UWU_AllowUltratechImpliedDesc".Translate());
                GUI.contentColor = prevContent;
                GUI.color = prevColor;
            }
            else
            {
                listing.CheckboxLabeled(
                    "UWU_AllowUltratech".Translate(),
                    ref Settings.allowUltratechCustomization,
                    "UWU_AllowUltratechDesc".Translate(UWU_ResearchDefOf.UniqueFabrication.label));
            }

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_AllowArchotech".Translate(),
                ref Settings.allowArchotechCustomization,
                "UWU_AllowArchotechDesc".Translate(UWU_ResearchDefOf.UniqueFabrication.label));

            listing.Gap(24.0f);

            Text.Font = GameFont.Medium;
            listing.Label("UWU_SettingsHaulPlanner".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(6f);

            DrawHaulPlannerOption(listing,
                HaulPlannerKind.Sequential,
                "UWU_HaulPlannerSequential".Translate() + "UWU_VanillaSuffix".Translate(),
                "UWU_HaulPlannerSequentialDesc".Translate(),
                enabled: true);

            DrawHaulPlannerOption(listing,
                HaulPlannerKind.Sweep,
                "UWU_HaulPlannerSweep".Translate() + "UWU_DefaultSuffix".Translate(),
                "UWU_HaulPlannerSweepDesc".Translate(),
                enabled: true);

            DrawHaulPlannerOption(listing,
                HaulPlannerKind.Thorough,
                "UWU_HaulPlannerThorough".Translate() + "UWU_ExperimentalSuffix".Translate(),
                "UWU_HaulPlannerThoroughDesc".Translate(),
                enabled: true);

            listing.Gap(24.0f);

            Text.Font = GameFont.Medium;
            listing.Label("UWU_SettingsMiscellaneous".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(6f);

            listing.CheckboxLabeled(
                "UWU_EnableGroundCustomization".Translate(),
                ref Settings.enableGroundCustomization,
                "UWU_EnableGroundCustomizationDesc".Translate());

            listing.Gap();

            if (ModsConfig.IdeologyActive)
            {
                listing.CheckboxLabeled(
                    "UWU_EnableIdeoColors".Translate(),
                    ref Settings.enableIdeologyColors,
                    "UWU_EnableIdeoColorsDesc".Translate());

                listing.Gap();
            }

            listing.CheckboxLabeled(
                "UWU_EnableStructureColors".Translate(),
                ref Settings.enableStructureColors,
                "UWU_EnableStructureColorsDesc".Translate());

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_EnforceMaxTraitLimit".Translate(),
                ref Settings.enforceMaxTraitLimit,
                "UWU_EnforceMaxTraitLimitDesc".Translate());

            listing.Gap();

            listing.CheckboxLabeled(
                "UWU_EnforceSoleTrait".Translate(),
                ref Settings.enforceCanGenerateAlone,
                "UWU_EnforceSoleTraitDesc".Translate());

            listing.Gap(60f);

            Text.Font = prev;
            settingsHeight = listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();

            if (Widgets.ButtonText(buttonRect, "UWU_ResetToDefaults".Translate()))
            {
                Settings.ResetToDefaults();
            }
        }

        // Renders one row of the haul-planner radio group. Disabled options
        // render darkened and ignore clicks. Selecting an enabled option flips
        // Settings.haulPlannerKind to that value. The label is passed in fully
        // composed (including any "(default)" / "(vanilla)" suffix).
        private static void DrawHaulPlannerOption(
            Listing_Standard listing,
            HaulPlannerKind kind,
            string label,
            string tooltip,
            bool enabled)
        {
            bool active = Settings.haulPlannerKind == kind;

            if (enabled)
            {
                if (listing.RadioButton(label, active, tabIn: 0f, tooltip: tooltip))
                {
                    Settings.haulPlannerKind = kind;
                }
            }
            else
            {
                Color prevColor = GUI.color;
                Color prevContent = GUI.contentColor;
                // Compound the tint: GUI.color attenuates the whole control,
                // GUI.contentColor specifically attenuates text/icon glyphs.
                // Together they multiply (~0.4 * 0.5 = 0.2 effective), which
                // reads as visibly darker than plain Color.gray would.
                GUI.color = new Color(0.4f, 0.4f, 0.4f);
                GUI.contentColor = new Color(0.5f, 0.5f, 0.5f);
                // Force-render as inactive even if Settings somehow points
                // here (e.g. via a save from a future build); the runtime
                // factory falls back to Sequential for unrecognized values
                // anyway, so showing it inactive here matches behavior.
                listing.RadioButton(label, active: false, tabIn: 0f, tooltip: tooltip);
                GUI.contentColor = prevContent;
                GUI.color = prevColor;
            }
            listing.Gap(8f);
        }
    }
}

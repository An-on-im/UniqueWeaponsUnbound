using RimWorld;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;

namespace UniqueWeaponsUnbound
{
    public class UWU_Settings : ModSettings
    {
        // Progression
        public bool restrictTraitsToDiscovered;

        // Trait Costs
        public bool useRecipeBaseCost = true;
        public float traitCostMultiplier = 1f;
        public float traitRefundRate = 0.5f;

        // Prerequisites
        public QualityCategory minimumQuality = QualityCategory.Awful;
        public bool allowDefConversion = true;
        public bool requireCustomizationResearch = true;
        public bool requireRecipeResearch = true;
        public bool requireAppropriateWorkbench = true;
        public bool allowUncraftableCustomization = true;
        public bool allowUltratechCustomization = true;
        public bool allowArchotechCustomization;

        // Haul Planner
        public HaulPlannerKind haulPlannerKind = HaulPlannerKind.Sweep;

        // Miscellaneous
        public bool enableGroundCustomization = true;
        public bool enableIdeologyColors = true;
        public bool enableStructureColors = true;
        public bool enforceMaxTraitLimit = true;
        public bool enforceCanGenerateAlone;

        public void ResetToDefaults()
        {
            restrictTraitsToDiscovered = false;

            useRecipeBaseCost = true;
            traitCostMultiplier = 1f;
            traitRefundRate = 0.5f;

            minimumQuality = QualityCategory.Awful;
            allowDefConversion = true;
            requireCustomizationResearch = true;
            requireRecipeResearch = true;
            requireAppropriateWorkbench = true;
            allowUncraftableCustomization = true;
            allowUltratechCustomization = true;
            allowArchotechCustomization = false;

            haulPlannerKind = HaulPlannerKind.Sweep;

            enableGroundCustomization = true;
            enableIdeologyColors = true;
            enableStructureColors = true;
            enforceMaxTraitLimit = true;
            enforceCanGenerateAlone = false;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref restrictTraitsToDiscovered, "restrictTraitsToDiscovered");

            Scribe_Values.Look(ref useRecipeBaseCost, "useRecipeBaseCost", true);
            Scribe_Values.Look(ref traitCostMultiplier, "traitCostMultiplier", 1f);
            Scribe_Values.Look(ref traitRefundRate, "traitRefundRate", 0.5f);

            Scribe_Values.Look(ref minimumQuality, "minimumQuality", QualityCategory.Awful);
            Scribe_Values.Look(ref allowDefConversion, "allowDefConversion", true);
            Scribe_Values.Look(ref requireCustomizationResearch, "requireCustomizationResearch", true);
            Scribe_Values.Look(ref requireRecipeResearch, "requireRecipeResearch", true);
            Scribe_Values.Look(ref requireAppropriateWorkbench, "requireAppropriateWorkbench", true);
            Scribe_Values.Look(ref allowUncraftableCustomization, "allowUncraftableCustomization", true);
            Scribe_Values.Look(ref allowUltratechCustomization, "allowUltratechCustomization", true);
            Scribe_Values.Look(ref allowArchotechCustomization, "allowArchotechCustomization");

            Scribe_Values.Look(ref haulPlannerKind, "haulPlannerKind", HaulPlannerKind.Sweep);

            Scribe_Values.Look(ref enableGroundCustomization, "enableGroundCustomization", true);
            Scribe_Values.Look(ref enableIdeologyColors, "enableIdeologyColors", true);
            Scribe_Values.Look(ref enableStructureColors, "enableStructureColors", true);
            Scribe_Values.Look(ref enforceMaxTraitLimit, "enforceMaxTraitLimit", true);
            Scribe_Values.Look(ref enforceCanGenerateAlone, "enforceCanGenerateAlone");
        }
    }
}

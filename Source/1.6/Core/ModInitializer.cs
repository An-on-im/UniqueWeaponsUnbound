using HarmonyLib;
using Verse;

namespace UniqueWeaponsUnbound
{
    [StaticConstructorOnStartup]
    public static class UniqueWeaponsUnboundMod
    {
        static UniqueWeaponsUnboundMod()
        {
            var harmony = new Harmony("shunter.uniqueweaponsunbound");
            harmony.PatchAll();

            var report = new InitDiagnostics();
            WeaponRegistry.Initialize(report);
            WorkbenchUtility.Initialize(report);
            TraitCostUtility.Initialize(report);
            WeaponModificationUtility.VerifyReflection();
            EquippableAbilityUtility.VerifyReflection();

            // Force the optional-mod integrations to resolve now, so any API drift is
            // reported during startup rather than lazily on first use (availability
            // depends only on what's loaded, no game state). Each one's static ctor
            // self-reports. VEFRecipeInheritanceIntegration needs no probe — it's
            // already touched at load by WorkbenchUtility.Initialize.
            _ = VEFWeaponTraitGraphicsIntegration.Available;
            _ = AlphaArmouryIntegration.Available;

            report.LogSummary();
        }
    }
}

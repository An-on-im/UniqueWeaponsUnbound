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
            report.LogSummary();
        }
    }
}

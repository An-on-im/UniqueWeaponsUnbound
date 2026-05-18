using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Entry point 3: right-click a weapon on the ground to customize it.
    /// Auto-selects the best workbench via <see cref="WorkbenchUtility.FindBestWorkbench"/>.
    /// </summary>
    public class FloatMenuOptionProvider_CustomizeGroundWeapon : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        protected override FloatMenuOption GetSingleOptionFor(
            Thing clickedThing, FloatMenuContext context)
        {
            // Outer guard so an unexpected throw inside the analysis (broken
            // building def during workbench search, modded weapon throwing
            // inside LabelShortCap, upstream cache NRE, etc.) drops only the
            // option instead of cascading into vanilla's menu construction.
            try
            {
                return BuildOption(clickedThing, context);
            }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Skipped ground-customization menu entry for "
                    + SafeLabel(clickedThing) + " ("
                    + (clickedThing?.def?.defName ?? "?") + ") due to error: " + ex);
                return null;
            }
        }

        private static FloatMenuOption BuildOption(Thing clickedThing, FloatMenuContext context)
        {
            if (UWU_Mod.Settings == null || !UWU_Mod.Settings.enableGroundCustomization)
                return null;
            if (clickedThing == null || clickedThing.def == null)
                return null;
            if (clickedThing is Building)
                return null;
            if (!clickedThing.def.IsWeapon)
                return null;
            if (!clickedThing.Spawned)
                return null;

            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null)
                return null;

            Thing weapon = clickedThing;

            // Variant exists + UniqueSmithing gate
            AcceptanceReport customizable = CustomizationRules.IsCustomizable(weapon);
            if (!customizable.Accepted && customizable.Reason.NullOrEmpty())
                return null;

            // Resolve base/unique defs
            WeaponRegistry.ResolveWeaponDefs(weapon,
                out ThingDef baseDef, out ThingDef uniqueDef);

            TechLevel weaponTechLevel = CustomizationRules.GetWeaponTechLevel(weapon);

            // Recipe research (craftability) — cheap O(1) check before workbench search
            AcceptanceReport craftable = CustomizationRules.GetCraftabilityReport(baseDef, uniqueDef);
            if (!craftable.Accepted)
                return DisabledOrHidden(weapon, craftable);

            // Customization research
            if (!customizable.Accepted)
                return DisabledOrHidden(weapon, customizable);

            string label = "UWU_CustomizeWeapon".Translate(weapon.LabelShortCap);

            // Weapon reachability + forbidden checks
            if (!pawn.CanReach(weapon, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                return new FloatMenuOption(
                    label + " (" + "NoPath".Translate() + ")",
                    null);
            }

            // No forbidden check on the weapon itself — this is a direct
            // player order, matching vanilla's behavior for equipping forbidden weapons.

            // Find best workbench (most expensive check — runs last)
            var result = WorkbenchUtility.FindBestWorkbench(
                pawn, baseDef, uniqueDef, weaponTechLevel, weapon.Position);

            if (!result.Found)
                return DisabledOrHidden(weapon, result.BestRejection);

            Building_WorkTable workbench = result.Workbench;

            // Capture for the click delegate so a destroyed-mid-menu weapon
            // doesn't NRE inside vanilla's order dispatch.
            Thing capturedWeapon = weapon;
            Building_WorkTable capturedWorkbench = workbench;
            Pawn capturedPawn = pawn;

            return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(
                    label,
                    delegate
                    {
                        FloatMenuOptionProvider_CustomizeWeapon.TryQueueCustomizeJob(
                            capturedPawn, capturedWeapon, capturedWorkbench);
                    }),
                pawn, weapon);
        }

        private static string SafeLabel(Thing t)
        {
            if (t == null) return "(null)";
            try { return t.LabelShortCap; }
            catch { return t.def?.defName ?? "(unlabelled)"; }
        }

        private static FloatMenuOption DisabledOrHidden(Thing weapon, AcceptanceReport report)
        {
            if (report.Reason.NullOrEmpty())
                return null;

            string label = "UWU_CustomizeWeapon".Translate(weapon.LabelShortCap)
                + " (" + report.Reason + ")";
            return new FloatMenuOption(label, null);
        }
    }
}

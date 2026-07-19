using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    public class FloatMenuOptionProvider_CustomizeWeapon : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(
            Thing clickedThing, FloatMenuContext context)
        {
            // Materialize options outside the iterator. yield return forbids
            // try/catch around it, so a throw inside GetOptionForWeapon would
            // abort the iterator and silently drop every remaining option for
            // the pawn (equipped + inventory). Building into a list first lets
            // us isolate per-weapon failures.
            List<FloatMenuOption> options = null;
            try
            {
                options = BuildOptions(clickedThing, context);
            }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Skipped customization menu construction at "
                    + (clickedThing?.LabelShortCap ?? "(null)") + " due to error: " + ex);
            }

            if (options == null)
                yield break;
            foreach (FloatMenuOption opt in options)
                yield return opt;
        }

        private static List<FloatMenuOption> BuildOptions(
            Thing clickedThing, FloatMenuContext context)
        {
            if (!(clickedThing is Building_WorkTable workbench))
                return null;

            bool isCustomizationBench;
            try
            {
                isCustomizationBench = WorkbenchUtility.IsCustomizationWorkbench(workbench);
            }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Workbench classification failed for "
                    + (workbench.def?.defName ?? "(null def)") + ": " + ex);
                return null;
            }
            if (!isCustomizationBench)
                return null;

            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null)
                return null;

            var options = new List<FloatMenuOption>();

            // Entry point 1: equipped weapon
            Thing equipped = pawn.equipment?.Primary;
            if (equipped != null)
                TryAddOption(options, pawn, equipped, workbench);

            // Entry point 2: inventory weapons.
            if (pawn.inventory?.innerContainer != null)
            {
                foreach (Thing item in pawn.inventory.innerContainer)
                {
                    if (item?.def == null || !item.def.IsWeapon)
                        continue;
                    TryAddOption(options, pawn, item, workbench);
                }
            }

            return options;
        }

        // Builds the option for one weapon and appends on success. Per-weapon
        // failures are isolated and logged so a single broken weapon (modded
        // stuff/quality throw, upstream cache NRE, etc.) can't suppress the
        // other entries.
        private static void TryAddOption(
            List<FloatMenuOption> options, Pawn pawn, Thing weapon, Building_WorkTable workbench)
        {
            try
            {
                FloatMenuOption option = GetOptionForWeapon(pawn, weapon, workbench);
                if (option != null)
                    options.Add(option);
            }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Skipped customization menu entry for "
                    + SafeLabel(weapon) + " (" + (weapon?.def?.defName ?? "?")
                    + ") due to error: " + ex);
            }
        }

        private static FloatMenuOption GetOptionForWeapon(
            Pawn pawn, Thing weapon, Building_WorkTable workbench)
        {
            // Variant exists + UniqueSmithing gate
            AcceptanceReport customizable = CustomizationRules.IsCustomizable(weapon);
            if (!customizable.Accepted && customizable.Reason.NullOrEmpty())
                return null;

            // Resolve base/unique defs for workbench and craftability checks
            WeaponRegistry.ResolveWeaponDefs(weapon,
                out ThingDef baseDef, out ThingDef uniqueDef);

            // Workbench: recipe match, then tech-level tier fallback
            TechLevel weaponTechLevel = CustomizationRules.GetWeaponTechLevel(weapon);
            AcceptanceReport workbenchReport = WorkbenchUtility.CanCustomizeAtWorkbench(
                baseDef, uniqueDef, weaponTechLevel, workbench);
            if (!workbenchReport.Accepted)
                return DisabledOrHidden(weapon, workbenchReport);

            // Workbench operational (power/fuel)
            AcceptanceReport operational = WorkbenchUtility.GetWorkbenchOperationalReport(workbench);
            if (!operational.Accepted)
                return DisabledOrHidden(weapon, operational);

            // Recipe research (craftability)
            AcceptanceReport craftable = CustomizationRules.GetCraftabilityReport(baseDef, uniqueDef);
            if (!craftable.Accepted)
                return DisabledOrHidden(weapon, craftable);

            // Customization research
            if (!customizable.Accepted)
                return DisabledOrHidden(weapon, customizable);

            string label = "UWU_CustomizeWeapon".Translate(weapon.LabelShortCap);

            if (!pawn.CanReach(workbench, PathEndMode.InteractionCell, Danger.Deadly))
            {
                return new FloatMenuOption(
                    label + " (" + "NoPath".Translate() + ")",
                    null);
            }

            if (workbench.IsForbidden(pawn))
            {
                return new FloatMenuOption(
                    label + " (" + "ForbiddenLower".Translate() + ")",
                    null);
            }

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
                        TryQueueCustomizeJob(capturedPawn, capturedWeapon, capturedWorkbench);
                    }),
                pawn, workbench);
        }

        // Click-delegate handler. Wrapped in try/catch so a missing JobDef or
        // any other unexpected failure surfaces as a player-visible message
        // rather than a silent no-op on the order.
        internal static void TryQueueCustomizeJob(
            Pawn pawn, Thing weapon, Building_WorkTable workbench)
        {
            try
            {
                Job job = JobMaker.MakeJob(UWU_JobDefOf.UWU_CustomizeWeapon);
                job.targetB = weapon;
                job.targetC = workbench;
                job.count = 1;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception ex)
            {
                Log.Error("[Unique Weapons Unbound] Failed to queue customization job for "
                    + SafeLabel(weapon) + ": " + ex);
                Messages.Message("UWU_CustomizeWeapon".Translate(SafeLabel(weapon))
                    + " (" + "Error".Translate() + ")",
                    weapon ?? (Thing)workbench,
                    MessageTypeDefOf.RejectInput, historical: false);
            }
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

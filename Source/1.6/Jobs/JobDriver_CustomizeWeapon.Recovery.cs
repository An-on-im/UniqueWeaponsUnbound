using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    // Recovery phase: cleanup that runs when the job ends, whether
    // successfully or via interruption. Drops any haul-phase inventory
    // the pawn was still holding so ingredients don't ride into the
    // next job, and queues a follow-up Equip/TakeInventory job so the
    // finished weapon ends up back where it started (matching returnMode).
    public partial class JobDriver_CustomizeWeapon
    {
        /// <summary>
        /// Drops haul-phase inventory items the pawn is still holding when
        /// the job ends (interrupted between pickup and workbench unload).
        /// Without this, the pawn would silently carry the ingredients into
        /// future jobs — confusing for the player and effectively a stockpile
        /// leak from the world's perspective.
        /// </summary>
        private void DropPendingHaulInventory()
        {
            if (currentTripInvLoad == null || currentTripInvLoad.Count == 0) return;
            if (pawn.Map == null || pawn.inventory == null) return;

            foreach (ThingDefCountClass entry in currentTripInvLoad)
            {
                int remaining = entry.count;
                for (int i = pawn.inventory.innerContainer.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    Thing inv = pawn.inventory.innerContainer[i];
                    if (inv.def != entry.thingDef) continue;
                    int dropAmt = Mathf.Min(remaining, inv.stackCount);
                    pawn.inventory.innerContainer.TryDrop(
                        inv, pawn.Position, pawn.Map, ThingPlaceMode.Near, dropAmt, out _);
                    remaining -= dropAmt;
                }
            }
            currentTripInvLoad.Clear();
        }

        /// <summary>
        /// Queues a follow-up job so the pawn walks to the weapon and picks it
        /// up via the standard equip/take-inventory job drivers. Used for both
        /// normal completion (pawn is at workbench, job completes near-instantly)
        /// and interruption recovery (pawn walks back to retrieve weapon).
        /// </summary>
        private void QueueWeaponRecovery()
        {
            if (weapon == null || weapon.Destroyed)
                return;

            if (pawn.Map == null)
                return;

            // Drop from carry if the pawn is still holding the weapon
            if (pawn.carryTracker?.CarriedThing == weapon)
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);

            if (!weapon.Spawned || weapon.Destroyed)
                return;

            switch (returnMode)
            {
                case WeaponReturnMode.Reequip:
                    pawn.jobs.jobQueue.EnqueueFirst(
                        JobMaker.MakeJob(JobDefOf.Equip, weapon));
                    break;

                case WeaponReturnMode.ReturnToInventory:
                    Job takeJob = JobMaker.MakeJob(JobDefOf.TakeInventory, weapon);
                    takeJob.count = 1;
                    pawn.jobs.jobQueue.EnqueueFirst(takeJob);
                    break;

                case WeaponReturnMode.LeaveOnWorkbench:
                    break;
            }
        }
    }
}

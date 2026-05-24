using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    // Haul phase: locate placement cells, drop/unload carry tracker and
    // inventory contents at the workbench, and route per-trip pickups
    // between the carry tracker and inventory according to the planner's
    // metadata. Populates placedIngredients (read by the Work phase) via
    // the TrackAndReservePlaced callback wired into every drop call.
    public partial class JobDriver_CustomizeWeapon
    {
        /// <summary>
        /// Finds the best cell to place an ingredient, preferring workbench cells
        /// (like vanilla's IngredientStackCells) before overflowing to nearby cells.
        /// </summary>
        private IntVec3 FindIngredientPlaceCell(Thing ingredient)
        {
            Map map = pawn.Map;
            IntVec3 interactionCell = Workbench.InteractionCell;

            // Prefer cells occupied by the workbench, closest to interaction cell first
            foreach (IntVec3 cell in GenAdj.CellsOccupiedBy(Workbench)
                .OrderBy(c => (c - interactionCell).LengthManhattan))
            {
                if (cell == interactionCell)
                    continue;
                Thing existing = cell.GetFirstItem(map);
                if (existing == null)
                    return cell;
                if (existing.CanStackWith(ingredient)
                    && existing.stackCount < existing.def.stackLimit
                    && pawn.CanReserve(existing))
                    return cell;
            }

            // Workbench cells full — fall back to center position (Near will radiate)
            return Workbench.Position;
        }

        /// <summary>
        /// Shared FailOn predicate for the goto-ingredient toil in both haul
        /// chains. Catches "ingredient gone forbidden / despawned / unreachable"
        /// before the pather hits its own silent Errored path.
        /// </summary>
        private bool GotoIngredientFailCondition()
        {
            Thing ing = job.GetTarget(IngredientIndex).Thing;
            if (ing == null || !ing.Spawned || ing.IsForbidden(pawn))
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                return true;
            }
            if (!pawn.CanReach(ing, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                SetBailMessage("UWU_BailIngredientUnreachable".Translate(WeaponLabel));
                return true;
            }
            return false;
        }

        /// <summary>
        /// VanillaCarryOnly drop: drops the carry-tracker contents at a
        /// workbench cell, falling back to a Near-mode placement if Direct
        /// fails. Bails if the carry tracker is unexpectedly empty —
        /// VanillaCarryOnly trips always pick up via the carry tracker, so
        /// an empty CT at drop time means an ingredient was lost mid-trip.
        /// </summary>
        private Toil BuildVanillaDropToil()
        {
            Toil drop = ToilMaker.MakeToil("HaulVanillaDrop");
            drop.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null)
                {
                    SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Action<Thing, int> placedAction = TrackAndReservePlaced;
                IntVec3 cell = FindIngredientPlaceCell(carried);
                if (pawn.carryTracker.TryDropCarriedThing(cell, ThingPlaceMode.Direct, out _, placedAction))
                    return;
                if (pawn.carryTracker.TryDropCarriedThing(Workbench.Position, ThingPlaceMode.Near, out _, placedAction))
                    return;
                SetBailMessage("UWU_BailIngredientPlacementFailed".Translate(WeaponLabel));
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                EndJobWith(JobCondition.Incompletable);
            };
            drop.defaultCompleteMode = ToilCompleteMode.Instant;
            return drop;
        }

        /// <summary>
        /// First toil of the UwuCarryInventoryHybrid path: pops the front of
        /// pickupDestinations and pickupLastInTrip into the per-pickup
        /// transient fields in lockstep with the just-extracted queue entry.
        /// </summary>
        private Toil BuildHybridSyncMetadataToil()
        {
            Toil sync = ToilMaker.MakeToil("HaulSyncMetadata");
            sync.initAction = delegate
            {
                if (pickupDestinations != null && pickupDestinations.Count > 0)
                {
                    currentPickupDestination = (PickupDestination)pickupDestinations[0];
                    pickupDestinations.RemoveAt(0);
                }
                else
                {
                    currentPickupDestination = PickupDestination.CarryTracker;
                }
                if (pickupLastInTrip != null && pickupLastInTrip.Count > 0)
                {
                    currentPickupLastInTrip = pickupLastInTrip[0];
                    pickupLastInTrip.RemoveAt(0);
                }
                else
                {
                    currentPickupLastInTrip = true;
                }
            };
            sync.defaultCompleteMode = ToilCompleteMode.Instant;
            return sync;
        }

        /// <summary>
        /// CT-destination pickup. Loads the requested count from the targeted
        /// stack into the pawn's carry tracker. The carry tracker is volume-
        /// bound (Pawn_CarryTracker.MaxStackSpaceEver) and mass-free.
        ///
        /// When the requested count exceeds carry-tracker volume (typical for
        /// SequentialHaulPlanner output, which doesn't volume-cap), we
        /// replicate vanilla Toils_Haul.StartCarryThing's putRemainderInQueue
        /// pattern: take what fits, re-insert the residual at the front of the
        /// queue (with parallel metadata) as its own trip, and end the current
        /// trip so the pawn drops at the bench before walking back for the
        /// rest. The residual rides with destination=CarryTracker so the next
        /// loop iteration handles it the same way; the source's reservation
        /// stays intact since we never call vanilla's auto-release path.
        /// </summary>
        private void DoCarryTrackerPickup()
        {
            Thing thing = job.GetTarget(IngredientIndex).Thing;
            if (thing == null || !thing.Spawned || thing.stackCount <= 0)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            int requested = job.count;
            int volAvail = pawn.carryTracker.AvailableStackSpace(thing.def);
            if (volAvail <= 0)
            {
                // CT is busy with a different def — would only happen if the
                // planner emitted two CT pickups for one trip, which it
                // doesn't (per-trip CT count is at most one). Defensive bail.
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            int take = Mathf.Min(requested, thing.stackCount, volAvail);
            if (take <= 0)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            int actualTaken = pawn.carryTracker.TryStartCarry(thing, take, reserve: true);
            if (actualTaken <= 0)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            int residual = requested - actualTaken;
            if (residual > 0)
            {
                if (job.targetQueueA == null) job.targetQueueA = new List<LocalTargetInfo>();
                if (job.countQueue == null) job.countQueue = new List<int>();
                if (pickupDestinations == null) pickupDestinations = new List<int>();
                if (pickupLastInTrip == null) pickupLastInTrip = new List<bool>();
                job.targetQueueA.Insert(0, thing);
                job.countQueue.Insert(0, residual);
                pickupDestinations.Insert(0, (int)PickupDestination.CarryTracker);
                pickupLastInTrip.Insert(0, true);
                currentPickupLastInTrip = true;
            }
        }

        /// <summary>
        /// Inventory-destination pickup. SplitOff the requested count from the
        /// targeted stack and TryAdd into the pawn's inventory. Inventory has
        /// no mass cap at the container level (mass is purely a movement-speed
        /// stat), so TryAdd succeeds unless the def is incompatible — defensive
        /// bail otherwise. Records the (def, count) for the trip-end unload to
        /// drop exactly this much at the workbench, ignoring any pre-existing
        /// inventory items of the same def.
        /// </summary>
        private void DoInventoryPickup()
        {
            Thing thing = job.GetTarget(IngredientIndex).Thing;
            if (thing == null || !thing.Spawned || thing.stackCount <= 0)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            int requested = job.count;
            if (thing.stackCount < requested)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            Thing splitOff = thing.SplitOff(requested);
            if (splitOff == null)
            {
                SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            if (!pawn.inventory.innerContainer.TryAdd(splitOff, canMergeWithExistingStacks: true))
            {
                if (splitOff != thing && !splitOff.Destroyed)
                    thing.TryAbsorbStack(splitOff, respectStackLimit: false);
                SetBailMessage("UWU_BailIngredientPlacementFailed".Translate(WeaponLabel));
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            if (currentTripInvLoad == null)
                currentTripInvLoad = new List<ThingDefCountClass>();
            currentTripInvLoad.Add(new ThingDefCountClass(thing.def, requested));
        }

        /// <summary>
        /// Drops whatever the pawn is carrying in the carry tracker at the
        /// workbench. No-op when the carry tracker is empty (inventory-only
        /// trips). Tracks placed stacks via placedAction for later consumption
        /// by ApplyOperation.
        /// </summary>
        private void UnloadCarryTrackerAtBench()
        {
            Thing carried = pawn.carryTracker.CarriedThing;
            if (carried == null) return;

            Action<Thing, int> placedAction = TrackAndReservePlaced;

            IntVec3 cell = FindIngredientPlaceCell(carried);
            if (pawn.carryTracker.TryDropCarriedThing(cell, ThingPlaceMode.Direct, out _, placedAction))
                return;
            if (pawn.carryTracker.TryDropCarriedThing(Workbench.Position, ThingPlaceMode.Near, out _, placedAction))
                return;

            // Workbench area has no room. Bail with a descriptive reason and
            // a final fallback drop so the carry tracker doesn't carry the
            // ingredient into the next job.
            SetBailMessage("UWU_BailIngredientPlacementFailed".Translate(WeaponLabel));
            pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            EndJobWith(JobCondition.Incompletable);
        }

        /// <summary>
        /// Drops the (def, count) entries the pawn loaded into inventory
        /// during the current trip at the workbench. Pre-existing inventory
        /// items of the same def stay where they are — only the trip's
        /// recorded loads are unloaded. Clears currentTripInvLoad on success.
        /// </summary>
        private void UnloadInventoryAtBench()
        {
            if (currentTripInvLoad == null || currentTripInvLoad.Count == 0)
                return;

            Action<Thing, int> placedAction = TrackAndReservePlaced;

            foreach (ThingDefCountClass entry in currentTripInvLoad)
            {
                int remaining = entry.count;
                for (int i = pawn.inventory.innerContainer.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    Thing inv = pawn.inventory.innerContainer[i];
                    if (inv.def != entry.thingDef) continue;

                    int dropAmt = Mathf.Min(remaining, inv.stackCount);
                    int stackBefore = inv.stackCount;
                    IntVec3 cell = FindIngredientPlaceCell(inv);
                    if (pawn.inventory.innerContainer.TryDrop(
                        inv, cell, pawn.Map, ThingPlaceMode.Direct, dropAmt, out _, placedAction))
                    {
                        remaining -= dropAmt;
                        continue;
                    }

                    // Direct mode may have partially absorbed our drop into
                    // an existing workbench-cell stack before failing —
                    // GenPlace.TryPlaceDirect calls TryAbsorbStack with
                    // respectStackLimit=true, which absorbs up to the cell
                    // stack's room then aborts when the cell is otherwise
                    // full. Credit that partial work and resize the retry
                    // before re-entering TryDrop, otherwise we'd request the
                    // original count against a now-smaller inv stack and
                    // trigger ThingOwner's "tried to drop X while only
                    // having Y" error log.
                    int absorbedDuringDirect = stackBefore - inv.stackCount;
                    remaining -= absorbedDuringDirect;
                    if (remaining <= 0) continue;

                    int retryAmt = Mathf.Min(remaining, inv.stackCount);
                    if (retryAmt <= 0) continue;
                    if (pawn.inventory.innerContainer.TryDrop(
                        inv, Workbench.Position, pawn.Map, ThingPlaceMode.Near, retryAmt, out _, placedAction))
                    {
                        remaining -= retryAmt;
                        continue;
                    }
                    SetBailMessage("UWU_BailIngredientPlacementFailed".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
            }
            currentTripInvLoad.Clear();
        }

        /// <summary>
        /// Drop callback shared by both unload paths. Adds the placed stack to
        /// placedIngredients (deduped) so ApplyOperation can consume from it
        /// later, and reserves it against this job so other haul AI doesn't
        /// pick it back up between trips. Reservation is best-effort — if
        /// CanReserve fails (e.g. another pawn already grabbed the cell stack
        /// before the absorb completed), we let it ride; the existing
        /// "ingredients lost" precheck will catch a real shortfall at op time.
        /// </summary>
        private void TrackAndReservePlaced(Thing placed, int _)
        {
            if (placed == null) return;
            if (!placedIngredients.Contains(placed))
                placedIngredients.Add(placed);
            if (placed.Spawned
                && !pawn.Map.reservationManager.ReservedBy(placed, pawn, job)
                && pawn.CanReserve(placed))
            {
                pawn.Reserve(placed, job, 1, -1, null, errorOnFailed: false);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace UniqueWeaponsUnbound
{
    // Customization job driver. Split into partial files by job phase:
    //
    //   .Haul.cs     - pickup/unload routing, drop-cell selection,
    //                  placedIngredients population (TrackAndReservePlaced)
    //   .Work.cs     - placedIngredients/refundLedger reads + writes,
    //                  per-op trait/cosmetics/color mutation, base<->unique
    //                  def conversion
    //   .Recovery.cs - end-of-job cleanup: drop unloaded inventory, queue
    //                  Equip/TakeInventory follow-up so the weapon returns
    //                  to where it started
    //
    // This file owns the driver scaffolding: scribed state, lifecycle
    // hooks, GetReport, the toil chain (MakeNewToils), and the bail-
    // message plumbing every phase routes through.
    public partial class JobDriver_CustomizeWeapon : JobDriver
    {
        private const TargetIndex IngredientIndex = TargetIndex.A;
        private const TargetIndex WeaponIndex = TargetIndex.B;
        private const TargetIndex WorkbenchIndex = TargetIndex.C;
        private const int WorkTicksPerOp = 1000;

        private CustomizationSpec spec;
        private Thing weapon;
        private int currentOpIndex;
        private string phaseReport;
        private WeaponReturnMode returnMode;
        private Dictionary<ThingDef, float> refundLedger = new Dictionary<ThingDef, float>();
        private List<Thing> placedIngredients = new List<Thing>();
        // First bail reason (translated, ready to surface). Read by the finish action
        // when the job ends with Incompletable. First-set wins so downstream cascade
        // failures don't overwrite the original cause.
        private string bailMessage;

        // Selects which haul-phase toil chain MakeNewToils builds for this
        // job. Set by IngredientReservation from the planner's HaulPlan and
        // scribed so mid-haul saves resume on the same path. Defaults to
        // VanillaCarryOnly so saves predating this field (and any plan that
        // never set the strategy) take the bedrock vanilla-toil path.
        private HaulPlanExecutionStrategy executionStrategy = HaulPlanExecutionStrategy.VanillaCarryOnly;

        // Per-pickup metadata populated by IngredientReservation alongside
        // job.targetQueueA / countQueue. Used only by the
        // UwuCarryInventoryHybrid execution path; the VanillaCarryOnly path
        // never reads these.
        private List<int> pickupDestinations;
        private List<bool> pickupLastInTrip;

        // Trip-scoped state, populated as the loop consumes pickup metadata
        // and consumed at trip end. Scribed: JobDriver.curToilIndex is scribed
        // by base, so a mid-trip reload resumes between hybrid sync and pickup
        // branch (or between pickup and trip-end unload) where these fields
        // would otherwise default to CarryTracker / false and mis-route the
        // next pickup or prematurely jump out of the trip.
        private PickupDestination currentPickupDestination;
        private bool currentPickupLastInTrip;
        // (def, count) entries the pawn loaded into inventory during the
        // current trip; consumed by the trip-end unload toil to drop exactly
        // those amounts at the workbench (ignoring any pre-existing inventory
        // stacks of the same def).
        private List<ThingDefCountClass> currentTripInvLoad;

        private Building_WorkTable Workbench =>
            (Building_WorkTable)job.GetTarget(WorkbenchIndex).Thing;

        /// <summary>
        /// Best label for the weapon: the live Thing if we have one, otherwise the
        /// job target (still labelled even if despawned), otherwise a fallback.
        /// Stays valid through every bail path including pre-acquire failures.
        /// </summary>
        private string WeaponLabel
        {
            get
            {
                if (weapon != null && !weapon.Destroyed)
                    return weapon.LabelShortCap;
                Thing target = job?.GetTarget(WeaponIndex).Thing;
                if (target != null)
                    return target.LabelShortCap;
                return "UWU_WeaponFallback".Translate();
            }
        }

        /// <summary>
        /// Records the first bail reason. Subsequent calls are ignored so a primary
        /// failure isn't overwritten by a downstream cascade. The recorded text is
        /// surfaced as a top-left Messages.Message when the finish action runs.
        /// </summary>
        private void SetBailMessage(string text)
        {
            if (string.IsNullOrEmpty(bailMessage))
                bailMessage = text;
        }

        /// <summary>
        /// Records the standard "ingredients lost mid-customization" bail message
        /// for the given trait. Used by the precheck toil and by the per-op consume
        /// paths in <see cref="ApplyOperation"/>.
        /// </summary>
        private void RecordShortfallBail(WeaponTraitDef trait)
        {
            string traitLabel = trait?.LabelCap ?? "";
            SetBailMessage("UWU_IngredientShortfall".Translate(WeaponLabel, traitLabel));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // Haul-phase: strategy + parallel pickup metadata, per-pickup
            // transient fields read by the hybrid pickup branch, trip-scoped
            // inventory load consumed at trip end, and cross-trip placed
            // ingredients consumed by the work loop.
            Scribe_Values.Look(ref executionStrategy, "executionStrategy",
                HaulPlanExecutionStrategy.VanillaCarryOnly);
            Scribe_Collections.Look(ref pickupDestinations, "pickupDestinations", LookMode.Value);
            Scribe_Collections.Look(ref pickupLastInTrip, "pickupLastInTrip", LookMode.Value);
            Scribe_Values.Look(ref currentPickupDestination, "currentPickupDestination",
                PickupDestination.CarryTracker);
            Scribe_Values.Look(ref currentPickupLastInTrip, "currentPickupLastInTrip", false);
            Scribe_Collections.Look(ref currentTripInvLoad, "currentTripInvLoad", LookMode.Deep);
            Scribe_Collections.Look(ref placedIngredients, "placedIngredients", LookMode.Reference);

            // Work-loop: spec/weapon/op-index/return-mode/refund-ledger.
            // Spec is Deep because the dialog writes it directly to this
            // field and there's no other source to recover it from. Weapon
            // is by reference so def conversions (which spawn a new Thing
            // and reassign the field) round-trip the post-conversion weapon,
            // not the destroyed pre-conversion one. Bail message rides along
            // so an end-of-toil save between EndJobWith and the finish action
            // still surfaces.
            Scribe_Deep.Look(ref spec, "spec");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref currentOpIndex, "currentOpIndex", 0);
            Scribe_Values.Look(ref returnMode, "returnMode", WeaponReturnMode.LeaveOnWorkbench);
            Scribe_Collections.Look(ref refundLedger, "refundLedger", LookMode.Def, LookMode.Value);
            Scribe_Values.Look(ref bailMessage, "bailMessage", null);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (placedIngredients == null)
                    placedIngredients = new List<Thing>();
                else
                    // Reference scribing resolves to null for Things that
                    // were destroyed/discarded between save and load.
                    placedIngredients.RemoveAll(t => t == null);
                if (refundLedger == null)
                    refundLedger = new Dictionary<ThingDef, float>();
            }
        }

        /// <summary>
        /// Called by Dialog_WeaponCustomization at confirm time, while the
        /// dialog's forcePause still holds the game still. Writes the spec to
        /// the scribed field directly so a save/reload taken in the one-tick
        /// gap between Close() and the consumeSpec toil round-trips a
        /// confirmed customization correctly.
        /// </summary>
        public void SetSpec(CustomizationSpec s)
        {
            spec = s;
        }

        /// <summary>
        /// Called by IngredientReservation after a plan is committed. Stores
        /// the execution strategy so MakeNewToils builds the right haul chain,
        /// plus per-pickup destination and trip-boundary flags in lockstep
        /// with job.targetQueueA / countQueue (used by the hybrid path only).
        /// </summary>
        public void SetHaulPickupMetadata(
            HaulPlanExecutionStrategy strategy,
            List<int> destinations,
            List<bool> lastInTripFlags)
        {
            executionStrategy = strategy;
            pickupDestinations = destinations;
            pickupLastInTrip = lastInTripFlags;
        }

        /// <summary>
        /// Passively heal orphaned ability caches at the earliest hook the
        /// JobDriver lifecycle exposes — fires once when the pawn starts the
        /// job, before <c>SetupToils</c>. A player who notices a phantom gizmo
        /// (e.g. LaunchSmokeShell carried over from a pre-fix base→unique
        /// conversion in an older save) just has to initiate customization on
        /// the affected weapon; the heal fires immediately, no need to wait
        /// for the pawn to walk anywhere, open the dialog, or confirm changes.
        /// Idempotent and a no-op when nothing's orphaned.
        ///
        /// The driver's <c>weapon</c> field isn't set until <c>acquireWeapon</c>
        /// runs, so we pull from the job target directly. <c>HealOrphanedAbility</c>
        /// null-checks and handles destroyed/non-unique weapons internally.
        /// </summary>
        public override void Notify_Starting()
        {
            base.Notify_Starting();
            // Heal runs before any toil and outside the finish-action bail
            // surface; an uncaught throw here would abort job start with no
            // UWU-prefixed log. Best-effort by design — swallow and proceed
            // so a broken heal can't block a legitimate customization order.
            try
            {
                EquippableAbilityUtility.HealOrphaned(
                    job.GetTarget(WeaponIndex).Thing);
            }
            catch (Exception ex)
            {
                Thing target = job?.GetTarget(WeaponIndex).Thing;
                string defName = target?.def?.defName ?? "(null)";
                Log.ErrorOnce(
                    "[Unique Weapons Unbound] Ability heal failed on job start for "
                        + defName + "; continuing without heal: " + ex,
                    ("UWU_HealOrphaned_" + defName).GetHashCode());
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(Workbench, job, 1, -1, null, errorOnFailed))
                return false;

            // Only reserve weapon if it's spawned on the map (ground weapon case).
            // Equipped/inventory weapons don't need map reservations.
            Thing w = job.GetTarget(WeaponIndex).Thing;
            if (w != null && w.Spawned
                && !pawn.Reserve(w, job, 1, -1, null, errorOnFailed))
                return false;

            return true;
        }

        public override string GetReport()
        {
            if (phaseReport != null)
                return phaseReport;

            string weaponLabel = weapon?.LabelShortCap
                ?? job.GetTarget(WeaponIndex).Thing?.LabelShortCap
                ?? "UWU_WeaponFallback".Translate();

            if (spec != null && currentOpIndex >= 0 && currentOpIndex < spec.operations.Count)
            {
                CustomizationOp op = spec.operations[currentOpIndex];
                switch (op.type)
                {
                    case OpType.AddTrait:
                        return "UWU_AddingTrait".Translate(
                            op.trait.LabelCap, weaponLabel);
                    case OpType.RemoveTrait:
                        return "UWU_RemovingTrait".Translate(
                            op.trait.LabelCap, weaponLabel);
                    case OpType.ApplyCosmetics:
                        return "UWU_ApplyingCosmetics".Translate(weaponLabel);
                }
            }

            return "UWU_CustomizingWeapon".Translate(weaponLabel);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Cache workbench comps for per-tick checks and fuel consumption
            CompPowerTrader powerComp = Workbench.TryGetComp<CompPowerTrader>();
            CompRefuelable fuelComp = Workbench.TryGetComp<CompRefuelable>();

            // Weapon destroyed mid-job. weapon is null during the acquire/carry phase;
            // only check once set. Checked before workbench so simultaneous losses
            // surface the weapon — the more player-relevant outcome.
            this.FailOn(() =>
            {
                if (weapon == null || !weapon.Destroyed)
                    return false;
                SetBailMessage("UWU_BailWeaponLost".Translate(WeaponLabel));
                return true;
            });
            // Workbench gone (despawned, destroyed, forbidden, or walled off).
            // Covers both gotoWorkbench calls (carrying weapon, and carrying the
            // ingredient back) plus any phase where the workbench must remain
            // reachable to continue.
            this.FailOn(() =>
            {
                Thing wb = job.GetTarget(WorkbenchIndex).Thing;
                if (wb == null || !wb.Spawned || wb.IsForbidden(pawn))
                {
                    SetBailMessage("UWU_BailWorkbenchUnavailable".Translate(WeaponLabel));
                    return true;
                }
                if (!pawn.CanReach(wb, PathEndMode.InteractionCell, Danger.Deadly))
                {
                    SetBailMessage("UWU_BailWorkbenchUnreachable".Translate(WeaponLabel));
                    return true;
                }
                return false;
            });
            // Workbench loses power or runs out of fuel mid-job.
            this.FailOn(() =>
            {
                bool inactive = (powerComp != null && !powerComp.PowerOn)
                    || (fuelComp != null && !fuelComp.HasFuel);
                if (inactive)
                    SetBailMessage("UWU_BailWorkbenchInactive".Translate(WeaponLabel));
                return inactive;
            });
            AddFinishAction(delegate (JobCondition condition)
            {
                // Drop any haul-phase inventory the pawn was still holding when
                // the job ended — without this, ingredients picked up but not
                // yet unloaded at the workbench would silently ride along into
                // the pawn's next job.
                DropPendingHaulInventory();

                // Spawn any remaining refund surplus (credits not consumed by additions)
                // Floor fractional amounts here — this is the only point where rounding occurs
                if (refundLedger.Count > 0 && pawn.Map != null)
                {
                    var surplus = new List<ThingDefCountClass>();
                    foreach (KeyValuePair<ThingDef, float> kv in refundLedger)
                    {
                        int whole = Mathf.FloorToInt(kv.Value);
                        if (whole > 0)
                            surplus.Add(new ThingDefCountClass(kv.Key, whole));
                    }
                    if (surplus.Count > 0)
                    {
                        Building bench = Workbench;
                        IntVec3 spawnPos = (bench != null && bench.Spawned)
                            ? bench.Position : pawn.Position;
                        WeaponModificationUtility.SpawnResourcesNear(
                            pawn.Map, spawnPos, surplus);
                    }
                    refundLedger.Clear();
                }

                // Surface the recorded bail reason as a transient top-left message.
                // Incompletable carries the recorded reason; Errored falls back to
                // a generic "unexpected" message so the player isn't left wondering
                // why the pawn stopped (the Log.Error at the assertion site has the
                // technical detail). Player-driven interrupts (drafting, cancelling)
                // use other JobConditions and stay silent.
                if (condition == JobCondition.Incompletable
                    && !string.IsNullOrEmpty(bailMessage))
                {
                    Messages.Message(bailMessage, pawn,
                        MessageTypeDefOf.NegativeEvent, historical: false);
                }
                else if (condition == JobCondition.Errored)
                {
                    Messages.Message(
                        "UWU_BailUnexpected".Translate(WeaponLabel), pawn,
                        MessageTypeDefOf.NegativeEvent, historical: false);
                }

                // On interruption, queue a follow-up job so the pawn walks
                // back to retrieve the weapon naturally (no teleporting).
                QueueWeaponRecovery();
            });

            // === ACQUIRE WEAPON ===
            // Derive returnMode from the weapon's current location and move it
            // into the pawn's carryTracker. For equipped/inventory weapons this
            // is a direct transfer (no ground drop). For ground weapons the
            // subsequent toils handle goto + pickup.

            Toil acquireWeapon = ToilMaker.MakeToil("MakeNewToils");
            acquireWeapon.initAction = delegate
            {
                Thing w = job.GetTarget(WeaponIndex).Thing;
                if (w == null || w.Destroyed)
                {
                    SetBailMessage("UWU_BailWeaponLost".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                // Customization is a direct player order on a specific weapon.
                // Clear any forbidden flag so the order isn't blocked by an
                // old hauling hint, and so the weapon stays available to other
                // pawns/AI after the job completes. No-op for non-spawned
                // (equipped/inventory) weapons.
                if (w.Spawned && w is ThingWithComps)
                    w.SetForbidden(false, warnOnFail: false);

                weapon = w;

                if (pawn.equipment?.Primary == w)
                {
                    returnMode = WeaponReturnMode.Reequip;
                    pawn.equipment.Remove((ThingWithComps)w);
                    if (!pawn.carryTracker.TryStartCarry(w))
                    {
                        pawn.equipment.AddEquipment((ThingWithComps)w);
                        SetBailMessage("UWU_BailCarryStartFailed".Translate(WeaponLabel));
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                }
                else if (pawn.inventory?.innerContainer?.Contains(w) == true)
                {
                    returnMode = WeaponReturnMode.ReturnToInventory;
                    pawn.inventory.innerContainer.Remove(w);
                    if (!pawn.carryTracker.TryStartCarry(w))
                    {
                        pawn.inventory.innerContainer.TryAdd(w);
                        SetBailMessage("UWU_BailCarryStartFailed".Translate(WeaponLabel));
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                }
                else if (w.Spawned)
                {
                    returnMode = WeaponReturnMode.LeaveOnWorkbench;
                    // Ground weapon — subsequent toils handle goto + pickup
                }
                else
                {
                    Log.Error("[Unique Weapons Unbound] Weapon is in unknown state at job start.");
                    EndJobWith(JobCondition.Errored);
                }
            };
            acquireWeapon.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return acquireWeapon;

            // If pawn is already carrying the weapon (equipped/inventory path),
            // skip the goto + pickup toils and jump straight to the workbench.
            Toil gotoWorkbench = Toils_Goto.GotoThing(WorkbenchIndex, PathEndMode.InteractionCell);

            yield return Toils_Jump.JumpIf(gotoWorkbench,
                () => pawn.carryTracker.CarriedThing != null);

            // Ground weapon path: walk to weapon, pick it up
            yield return Toils_Goto.GotoThing(WeaponIndex, PathEndMode.ClosestTouch)
                .FailOn(() =>
                {
                    Thing w = job.GetTarget(WeaponIndex).Thing;
                    bool gone = w == null || !w.Spawned || w.IsForbidden(pawn)
                        || !pawn.CanReach(w, PathEndMode.ClosestTouch, Danger.Deadly);
                    if (gone)
                        SetBailMessage("UWU_BailWeaponInaccessible".Translate(WeaponLabel));
                    return gone;
                });

            yield return Toils_Haul.StartCarryThing(WeaponIndex);

            // === HAUL TO WORKBENCH & PLACE ===

            yield return gotoWorkbench;

            Toil placeWeapon = ToilMaker.MakeToil("MakeNewToils");
            placeWeapon.initAction = delegate
            {
                string label = weapon?.LabelShortCap ?? "UWU_WeaponFallback".Translate();
                phaseReport = "UWU_PlacingWeapon".Translate(label, Workbench.LabelShortCap);

                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried != null)
                {
                    if (!pawn.carryTracker.TryDropCarriedThing(
                            Workbench.Position, ThingPlaceMode.Direct, out _))
                        pawn.carryTracker.TryDropCarriedThing(
                            Workbench.Position, ThingPlaceMode.Near, out _);

                    pawn.Reserve(weapon, job);
                    pawn.Map.physicalInteractionReservationManager.Reserve(pawn, job, weapon);
                }
            };
            placeWeapon.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return placeWeapon;

            // === DIALOG PHASE ===
            // The weapon is now on the workbench. Open the customization dialog.
            // The dialog's forcePause keeps the game paused while the player
            // makes choices. On the first tick after the dialog closes, we
            // check whether a spec was stored (confirm) or not (cancel).

            Toil waitForDialog = ToilMaker.MakeToil("MakeNewToils");
            waitForDialog.initAction = delegate
            {
                if (weapon == null || weapon.Destroyed)
                {
                    SetBailMessage("UWU_BailWeaponLost".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // Dialog ctor does heavy work (reflection, TraitProgressionPool
                // build, ColorDef enumerations, relic precept access). An
                // uncaught throw here would Error the job and surface only the
                // generic UWU_BailUnexpected; wrap so the player sees a
                // dialog-specific bail reason and the log carries the
                // weapon-context exception.
                try
                {
                    Find.WindowStack.Add(
                        new Dialog_WeaponCustomization(pawn, weapon, Workbench));
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Could not open customization dialog for "
                        + WeaponLabel + ": " + ex);
                    SetBailMessage("UWU_BailDialogOpenFailed".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            waitForDialog.tickAction = delegate
            {
                // forcePause prevents ticking while the dialog is open.
                // Once it closes and the game unpauses, check the outcome
                // via the spec field — Dialog_WeaponCustomization.Confirm
                // sets it directly on this driver before Close().
                if (spec != null)
                {
                    ReadyForNextToil();
                }
                else if (!Find.WindowStack.IsOpen<Dialog_WeaponCustomization>())
                {
                    // Dialog was cancelled — end job gracefully
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            waitForDialog.defaultCompleteMode = ToilCompleteMode.Never;
            yield return waitForDialog;

            // === CONSUME SPEC + FIND INGREDIENTS ===

            Toil consumeSpec = ToilMaker.MakeToil("MakeNewToils");
            consumeSpec.initAction = delegate
            {
                // Spec is set directly on this driver by the dialog at confirm
                // time; this toil is the boundary between dialog phase and
                // haul phase. Ingredients were reserved synchronously under
                // forcePause, so job.targetQueueA / countQueue are already
                // populated by the time this runs.
                if (spec == null)
                {
                    Log.Error("[Unique Weapons Unbound] Spec was null at job start.");
                    EndJobWith(JobCondition.Errored);
                    return;
                }
            };
            consumeSpec.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return consumeSpec;

            // === HAUL PHASE ===
            // targetQueueA is populated at runtime by the consume-spec toil,
            // so we use a jump to skip hauling when there are no ingredients
            // rather than a compile-time conditional.

            // Define startWorkLoop ahead so the jump can reference it
            Toil startWorkLoop = ToilMaker.MakeToil("MakeNewToils");
            startWorkLoop.initAction = delegate
            {
                currentOpIndex = 0;
                phaseReport = null; // Clear so per-op GetReport logic takes over
            };
            startWorkLoop.defaultCompleteMode = ToilCompleteMode.Instant;

            yield return Toils_Jump.JumpIf(startWorkLoop,
                () => job.targetQueueA == null || job.targetQueueA.Count == 0);

            Toil setHaulReport = ToilMaker.MakeToil("HaulSetReport");
            setHaulReport.initAction = delegate
            {
                string label = weapon?.LabelShortCap ?? "UWU_WeaponFallback".Translate();
                phaseReport = "UWU_GatheringMaterials".Translate(label);
                if (currentTripInvLoad == null)
                    currentTripInvLoad = new List<ThingDefCountClass>();
            };
            setHaulReport.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return setHaulReport;

            Toil extract = Toils_JobTransforms.ExtractNextTargetFromQueue(IngredientIndex);
            yield return extract;

            // === Strategy branch ===
            // Two haul-phase toil chains live below: the bedrock vanilla
            // path (matches pre-Sweep behavior exactly — Toils_Haul.StartCarryThing
            // + the original drop toil + JumpIfHaveTargetInQueue) and the
            // UWU hybrid path (carry-tracker + inventory routing with
            // metadata sync). The vanilla path falls through from this jump;
            // the hybrid path is reached when executionStrategy says so.
            // Both paths converge at rejoinLoop below.
            Toil hybridEntry = BuildHybridSyncMetadataToil();
            yield return Toils_Jump.JumpIf(hybridEntry,
                () => executionStrategy == HaulPlanExecutionStrategy.UwuCarryInventoryHybrid);

            // === VanillaCarryOnly path ===
            // Identical in shape to the pre-Sweep haul phase. No parallel-
            // list reads, no destination branching, no inventory pickups —
            // every Sequential plan rides this path exclusively.
            Toil rejoinLoop = Toils_Jump.JumpIfHaveTargetInQueue(IngredientIndex, extract);

            yield return Toils_Goto.GotoThing(IngredientIndex, PathEndMode.ClosestTouch)
                .FailOn(GotoIngredientFailCondition);

            yield return Toils_Haul.StartCarryThing(IngredientIndex, putRemainderInQueue: true);

            yield return Toils_Goto.GotoThing(WorkbenchIndex, PathEndMode.InteractionCell);

            yield return BuildVanillaDropToil();

            // Skip past the hybrid path to the loop tail.
            yield return Toils_Jump.JumpIf(rejoinLoop, () => true);

            // === UwuCarryInventoryHybrid path ===
            yield return hybridEntry;

            // Vanilla's Toils_Haul.StartCarryThing auto-releases the stack
            // reservation when curJob.count is fully satisfied but the stack
            // has remainder on the map. Hybrid plans split a single Thing
            // across multiple queue entries by design, so we re-acquire the
            // reservation before each subsequent visit. Race with another
            // pawn during walk-back is detected by the CanReserve check.
            Toil reReserveIfNeeded = ToilMaker.MakeToil("HaulReReserve");
            reReserveIfNeeded.initAction = delegate
            {
                Thing t = job.GetTarget(IngredientIndex).Thing;
                if (t == null || !t.Spawned)
                    return;
                if (pawn.Map.reservationManager.ReservedBy(t, pawn, job))
                    return;
                if (!pawn.CanReserve(t))
                {
                    SetBailMessage("UWU_BailIngredientLost".Translate(WeaponLabel));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                pawn.Reserve(t, job, 1, -1, null, errorOnFailed: false);
            };
            reReserveIfNeeded.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return reReserveIfNeeded;

            yield return Toils_Goto.GotoThing(IngredientIndex, PathEndMode.ClosestTouch)
                .FailOn(GotoIngredientFailCondition);

            // Carry tracker holds at most one def at a time, so every CT
            // pickup is by construction the trip's first (or only) pickup of
            // that def; inventory pickups stack across defs in the same trip.
            Toil pickupBranch = ToilMaker.MakeToil("HaulPickupBranch");
            pickupBranch.initAction = delegate
            {
                if (currentPickupDestination == PickupDestination.Inventory)
                    DoInventoryPickup();
                else
                    DoCarryTrackerPickup();
            };
            pickupBranch.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pickupBranch;

            // More pickups in this trip → loop back to extract without
            // detouring to the workbench.
            yield return Toils_Jump.JumpIf(extract, () => !currentPickupLastInTrip);

            yield return Toils_Goto.GotoThing(WorkbenchIndex, PathEndMode.InteractionCell);

            // Trip end: drop the carry tracker (if loaded) and unload the
            // inventory (matched by def/count entries we recorded during the
            // trip — pre-existing inventory items of the same def stay).
            Toil unloadAtWorkbench = ToilMaker.MakeToil("HaulUnloadAtWorkbench");
            unloadAtWorkbench.initAction = delegate
            {
                UnloadCarryTrackerAtBench();
                UnloadInventoryAtBench();
            };
            unloadAtWorkbench.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return unloadAtWorkbench;

            // === Rejoin ===
            yield return rejoinLoop;

            // === WORK LOOP ===
            // Each operation is applied after its work tick completes, so each
            // completed step leaves the weapon in a consistent state. Def
            // conversions (base↔unique) are bundled into the trait operation
            // that crosses the 0↔1 trait boundary.

            yield return startWorkLoop;

            // Pre-flight: verify the upcoming op can be paid before sinking
            // 1000 ticks of work into it. Catches placed-ingredient losses
            // (fire, deterioration, etc.) at the start of each iteration so the
            // pawn doesn't waste a full work cycle on a cost we already can't pay.
            Toil precheckOp = ToilMaker.MakeToil("MakeNewToils");
            precheckOp.initAction = delegate
            {
                if (spec == null || currentOpIndex >= spec.operations.Count)
                    return;
                CustomizationOp op = spec.operations[currentOpIndex];
                if (!CanAffordOpCost(op.cost))
                {
                    RecordShortfallBail(op.trait);
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            precheckOp.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return precheckOp;

            Toil workToil = Toils_General.Wait(WorkTicksPerOp, WorkbenchIndex);
            workToil.AddPreTickAction(delegate
            {
                if (fuelComp != null)
                    fuelComp.Notify_UsedThisTick();
            });
            workToil.WithProgressBar(WorkbenchIndex,
                () => 1f - (float)ticksLeftThisToil / WorkTicksPerOp);
            yield return workToil;

            Toil applyOp = ToilMaker.MakeToil("MakeNewToils");
            applyOp.initAction = delegate
            {
                if (currentOpIndex < spec.operations.Count)
                {
                    ApplyOperation(spec.operations[currentOpIndex]);
                    currentOpIndex++;
                }
            };
            applyOp.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return applyOp;

            yield return Toils_Jump.JumpIf(precheckOp, () => currentOpIndex < spec.operations.Count);

            // === FINALIZE ===

            Toil finalize = ToilMaker.MakeToil("MakeNewToils");
            finalize.initAction = delegate
            {
                // Each step is isolated so a throw doesn't Errored the job
                // and surface UWU_BailUnexpected — by this toil the weapon is
                // already traited and ingredients are consumed, so failure
                // here is post-success cleanup. Continue to admire + return
                // so the player walks away with the customized weapon; raise
                // a soft message naming what specifically didn't stick.

                // Final Setup call for ability prop wiring. Routed through the
                // utility so cosmetics-only customizations don't free-reload
                // unchanged ability traits — vanilla Setup() forces
                // RemainingCharges = MaxCharges on every ability trait it sees.
                try
                {
                    EquippableAbilityUtility.SyncToTraits(weapon);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Ability sync failed during finalize on "
                        + WeaponLabel + ": " + ex);
                    Messages.Message(
                        "UWU_FinalizeAbilitySyncFailed".Translate(WeaponLabel),
                        weapon, MessageTypeDefOf.NegativeEvent, historical: false);
                }

                // Apply final color after Setup() to ensure it sticks
                try
                {
                    CompUniqueWeapon uniqueComp = weapon.TryGetComp<CompUniqueWeapon>();
                    if (spec.finalColor != null && uniqueComp != null)
                        WeaponModificationUtility.SetColor(weapon, spec.finalColor);
                }
                catch (Exception ex)
                {
                    Log.Error("[Unique Weapons Unbound] Final color application failed on "
                        + WeaponLabel + ": " + ex);
                    Messages.Message(
                        "UWU_FinalizeColorFailed".Translate(WeaponLabel),
                        weapon, MessageTypeDefOf.NegativeEvent, historical: false);
                }
            };
            finalize.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finalize;

            // === PAUSE: brief moment before returning the finished weapon ===

            Toil admire = Toils_General.Wait(300, WorkbenchIndex);
            admire.initAction = delegate
            {
                phaseReport = "UWU_AdmiringWork".Translate();
            };
            yield return admire;

            // === RETURN WEAPON ===
            // Post-completion behavior is driven by returnMode, derived during
            // the acquire phase from the weapon's original location.

            Toil returnWeaponToil = ToilMaker.MakeToil("MakeNewToils");
            returnWeaponToil.initAction = delegate
            {
                // Null the field before recovery runs so the finish-action's
                // recovery call sees a null weapon and bails. If recovery
                // throws after queueing the follow-up job, the finish action
                // would otherwise re-queue and double-recover.
                Thing recoverWeapon = weapon;
                weapon = null;
                QueueWeaponRecoveryFor(recoverWeapon);
            };
            returnWeaponToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return returnWeaponToil;
        }

        // Phase-specific helpers live in JobDriver_CustomizeWeapon.{Haul,Work,Recovery}.cs
    }
}

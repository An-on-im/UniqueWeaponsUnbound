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
    public class JobDriver_CustomizeWeapon : JobDriver
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
        /// Consumes resources from the tracked placedIngredients list rather than
        /// scanning nearby cells. Mirrors vanilla's pattern of consuming from
        /// job.placedThings. Destroyed stacks are removed from the list.
        /// </summary>
        private bool ConsumeFromPlacedIngredients(List<ThingDefCountClass> costs)
        {
            foreach (ThingDefCountClass cost in costs)
            {
                int remaining = cost.count;
                for (int i = placedIngredients.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    Thing stack = placedIngredients[i];
                    if (stack.Destroyed || !stack.Spawned || stack.def != cost.thingDef)
                        continue;

                    int take = Mathf.Min(remaining, stack.stackCount);
                    remaining -= take;

                    if (take >= stack.stackCount)
                    {
                        stack.Destroy();
                        placedIngredients.RemoveAt(i);
                    }
                    else
                    {
                        stack.SplitOff(take).Destroy();
                    }
                }

                if (remaining > 0)
                {
                    Log.Warning($"[Unique Weapons Unbound] Could not consume all " +
                        $"{cost.thingDef.LabelCap} from placed ingredients: " +
                        $"needed {cost.count}, short by {remaining}.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the total reservable count of <paramref name="thingDef"/> across all
        /// currently placed ingredient stacks, ignoring destroyed/despawned ones.
        /// </summary>
        private int CountInPlaced(ThingDef thingDef)
        {
            int available = 0;
            for (int i = 0; i < placedIngredients.Count; i++)
            {
                Thing stack = placedIngredients[i];
                if (stack.Destroyed || !stack.Spawned || stack.def != thingDef)
                    continue;
                available += stack.stackCount;
            }
            return available;
        }

        /// <summary>
        /// Returns true if an op's cost could currently be paid from the refund
        /// ledger plus placed ingredients, without committing any state. Used as a
        /// pre-flight check before starting an op's work cycle so the pawn doesn't
        /// waste 1000 ticks of work on an op we already know will abort.
        /// </summary>
        private bool CanAffordOpCost(List<ThingDefCountClass> opCost)
        {
            if (opCost == null || opCost.Count == 0)
                return true;

            foreach (ThingDefCountClass cost in opCost)
            {
                int remaining = cost.count;
                if (refundLedger.TryGetValue(cost.thingDef, out float credit) && credit > 0f)
                    remaining -= Mathf.Min(remaining, Mathf.FloorToInt(credit));
                if (remaining > 0 && CountInPlaced(cost.thingDef) < remaining)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Pays an op's cost: debits the refund ledger first, then consumes the
        /// remainder from placed ingredient stacks at the workbench. Pre-checks
        /// availability and only commits if the cost can be fully paid, so a
        /// shortfall (e.g. ingredients destroyed by fire/explosion/deterioration)
        /// leaves the ledger and the weapon untouched. Returns false on shortfall —
        /// caller should notify the player and abort the job.
        /// </summary>
        private bool TryConsumeOpCost(List<ThingDefCountClass> opCost)
        {
            if (opCost == null || opCost.Count == 0)
                return true;

            // First pass: compute what we'd take from the ledger and from placed
            // ingredients, without committing.
            var fromPlaced = new List<ThingDefCountClass>();
            var pendingDebit = new Dictionary<ThingDef, int>();
            foreach (ThingDefCountClass cost in opCost)
            {
                int remaining = cost.count;
                if (refundLedger.TryGetValue(cost.thingDef, out float credit) && credit > 0f)
                {
                    int debit = Mathf.Min(remaining, Mathf.FloorToInt(credit));
                    if (debit > 0)
                    {
                        pendingDebit[cost.thingDef] = debit;
                        remaining -= debit;
                    }
                }
                if (remaining > 0)
                    fromPlaced.Add(new ThingDefCountClass(cost.thingDef, remaining));
            }

            // Verify the placed-ingredient remainder can be satisfied before
            // mutating any state.
            foreach (ThingDefCountClass need in fromPlaced)
            {
                if (CountInPlaced(need.thingDef) < need.count)
                    return false;
            }

            // Commit ledger debits and ingredient consumption.
            foreach (KeyValuePair<ThingDef, int> kv in pendingDebit)
                refundLedger[kv.Key] -= kv.Value;
            if (fromPlaced.Count > 0)
                ConsumeFromPlacedIngredients(fromPlaced);
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
            AddFinishAction(delegate(JobCondition condition)
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
                Find.WindowStack.Add(
                    new Dialog_WeaponCustomization(pawn, weapon, Workbench));
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
                // Final Setup call for ability prop wiring. Routed through the
                // utility so cosmetics-only customizations don't free-reload
                // unchanged ability traits — vanilla Setup() forces
                // RemainingCharges = MaxCharges on every ability trait it sees.
                EquippableAbilityUtility.SyncToTraits(weapon);

                // Apply final color after Setup() to ensure it sticks
                CompUniqueWeapon uniqueComp = weapon.TryGetComp<CompUniqueWeapon>();
                if (spec.finalColor != null && uniqueComp != null)
                    WeaponModificationUtility.SetColor(weapon, spec.finalColor);
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
                QueueWeaponRecovery();
                weapon = null; // Prevent finish action from double-recovering
            };
            returnWeaponToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return returnWeaponToil;
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

        private void ApplyOperation(CustomizationOp op)
        {
            try
            {
                ApplyOperationInner(op);
            }
            catch (Exception ex)
            {
                // The weapon may be in a partial state — e.g. cost paid but
                // trait not yet added, or trait added but ability comp not
                // wired. Continuing would compound the damage: a failed remove
                // leaves the trait in place (and no refund credit to the
                // ledger), so a subsequent add could push the count past the
                // trait limit and/or run short on materials the refund was
                // funding. Bail here; placed ingredients consumed by
                // TryConsumeOpCost prior to the throw are not recovered.
                RecordOpFailureBail(op, ex);
                EndJobWith(JobCondition.Incompletable);
            }
        }

        /// <summary>
        /// Records a structured log line plus a translated, op-type-specific
        /// bail message for an unexpected throw inside ApplyOperation. The log
        /// names the op index, op type, trait defName, and weapon defName so
        /// post-mortem triage doesn't have to reconstruct the failing op from
        /// the surrounding toil context. The bail message is routed through
        /// the first-set-wins <see cref="SetBailMessage"/> channel so a cascade
        /// failure can't overwrite the original cause.
        /// </summary>
        private void RecordOpFailureBail(CustomizationOp op, Exception ex)
        {
            string opDescr;
            string bailMessageText;
            switch (op.type)
            {
                case OpType.AddTrait:
                    opDescr = "adding trait " + (op.trait?.defName ?? "(null)");
                    bailMessageText = "UWU_BailOpAddTraitFailed".Translate(
                        WeaponLabel, op.trait?.LabelCap ?? "");
                    break;
                case OpType.RemoveTrait:
                    opDescr = "removing trait " + (op.trait?.defName ?? "(null)");
                    bailMessageText = "UWU_BailOpRemoveTraitFailed".Translate(
                        WeaponLabel, op.trait?.LabelCap ?? "");
                    break;
                case OpType.ApplyCosmetics:
                    opDescr = "applying cosmetics";
                    bailMessageText = "UWU_BailOpCosmeticsFailed".Translate(WeaponLabel);
                    break;
                default:
                    opDescr = "op type " + op.type;
                    bailMessageText = "UWU_BailUnexpected".Translate(WeaponLabel);
                    break;
            }

            int totalOps = spec?.operations?.Count ?? -1;
            string weaponDefName = weapon?.def?.defName
                ?? job?.GetTarget(WeaponIndex).Thing?.def?.defName
                ?? "(null)";
            Log.Error("[Unique Weapons Unbound] Customization aborted while " + opDescr
                + " on " + WeaponLabel + " [" + weaponDefName + "] "
                + "(op " + (currentOpIndex + 1) + "/" + totalOps + "): " + ex);
            SetBailMessage(bailMessageText);
        }

        private void ApplyOperationInner(CustomizationOp op)
        {
            switch (op.type)
            {
                case OpType.RemoveTrait:
                    // Negative-trait removals carry a cost (op.cost). Pay it before
                    // removing the trait so a placed-ingredient shortfall can't leave
                    // the trait already gone with no payment recorded.
                    if (!TryConsumeOpCost(op.cost))
                    {
                        RecordShortfallBail(op.trait);
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    WeaponModificationUtility.RemoveTrait(weapon, op.trait);

                    // Credit refund to the virtual ledger atomically with the removal.
                    // Raw costs are stored on the op; apply CostMultiplier and RefundRate
                    // here as float to defer rounding until resources are actually spawned
                    // or consumed.
                    if (op.refund != null)
                    {
                        foreach (ThingDefCountClass refund in op.refund)
                        {
                            float credit = refund.count
                                * TraitCostUtility.CostMultiplier
                                * TraitCostUtility.RefundRate;
                            if (refundLedger.ContainsKey(refund.thingDef))
                                refundLedger[refund.thingDef] += credit;
                            else
                                refundLedger[refund.thingDef] = credit;
                        }
                    }

                    // If removing the last trait, convert unique→base atomically
                    CompUniqueWeapon removeComp = weapon.TryGetComp<CompUniqueWeapon>();
                    if (removeComp != null && removeComp.TraitsListForReading.Count == 0
                        && UWU_Mod.Settings.allowDefConversion)
                    {
                        ThingDef baseDef = WeaponRegistry.GetBaseVariant(weapon.def);
                        if (baseDef != null)
                            ConvertWeaponInPlace(baseDef);
                    }
                    break;

                case OpType.ApplyCosmetics:
                    if (weapon.TryGetComp<CompUniqueWeapon>() != null)
                    {
                        if (op.nameToApply != null)
                            WeaponModificationUtility.SetName(weapon, op.nameToApply);
                        if (op.textureIndexToApply.HasValue)
                            WeaponModificationUtility.SetTextureIndex(weapon, op.textureIndexToApply.Value);
                    }
                    break;

                case OpType.AddTrait:
                    // Pay the cost first — if placed ingredients have been destroyed
                    // (fire, explosion, deterioration), abort cleanly before any
                    // mutation (def conversion, trait add) leaves a partial state.
                    if (!TryConsumeOpCost(op.cost))
                    {
                        RecordShortfallBail(op.trait);
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    // If weapon is currently base, convert base→unique first
                    if (!WeaponRegistry.IsUniqueWeapon(weapon.def) && UWU_Mod.Settings.allowDefConversion)
                    {
                        ThingDef uniqueDef = WeaponRegistry.GetUniqueVariant(weapon.def);
                        if (uniqueDef != null)
                            ConvertWeaponInPlace(uniqueDef);
                    }

                    WeaponModificationUtility.AddTrait(weapon, op.trait);

                    // Apply bundled cosmetics (merged from a cosmetics op that
                    // would have been a no-op when the weapon was in base state)
                    if (weapon.TryGetComp<CompUniqueWeapon>() != null)
                    {
                        if (op.nameToApply != null)
                            WeaponModificationUtility.SetName(weapon, op.nameToApply);
                        if (op.textureIndexToApply.HasValue)
                            WeaponModificationUtility.SetTextureIndex(weapon, op.textureIndexToApply.Value);
                    }
                    break;
            }

            // Apply color change if this op carries one
            if (weapon.TryGetComp<CompUniqueWeapon>() != null)
            {
                if (op.clearColor)
                    WeaponModificationUtility.SetColor(weapon, null);
                else if (op.colorToApply != null)
                    WeaponModificationUtility.SetColor(weapon, op.colorToApply);
            }
        }

        /// <summary>
        /// Converts the weapon to a different ThingDef in-place (base↔unique).
        /// Destroys the current weapon, spawns a new one at the same position,
        /// and updates reservations. Called atomically within an ApplyOperation
        /// step when a trait change crosses the 0↔1 boundary.
        /// </summary>
        private void ConvertWeaponInPlace(ThingDef targetDef)
        {
            Thing newWeapon = WeaponDefConversion.ConvertWeaponDef(weapon, targetDef);
            IntVec3 pos = weapon.Position;
            Map map = weapon.Map;

            // Transfer relic status BEFORE destroying the old weapon so that
            // Thing.Destroy() does not fire Notify_ThingLost on the precept.
            WeaponDefConversion.TransferRelicStatus(weapon, newWeapon);

            if (weapon.Spawned)
                weapon.Destroy();
            else if (!weapon.Destroyed)
                weapon.Destroy();

            // Apply desired texture on base→unique so it doesn't flash a random variant
            if (WeaponRegistry.IsUniqueWeapon(targetDef)
                && spec.finalTextureIndex.HasValue)
                WeaponModificationUtility.SetTextureIndex(newWeapon, spec.finalTextureIndex.Value);

            GenSpawn.Spawn(newWeapon, pos, map);
            pawn.Reserve(newWeapon, job);
            pawn.Map.physicalInteractionReservationManager.Reserve(pawn, job, newWeapon);
            weapon = newWeapon;
            // Keep the job target in sync with the live weapon: a save taken
            // after a conversion must not scribe targetB as a destroyed ref.
            job.SetTarget(WeaponIndex, newWeapon);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /// <summary>
    /// Bridge between the customization dialog and the IHaulPlanner pipeline.
    /// Counts ingredient availability for the dialog's per-row indicators,
    /// builds the candidate pool a planner will draw from, and commits the
    /// resulting plan to the job's target/count queues with reservations
    /// already held.
    ///
    /// Designed to be callable while the dialog's forcePause is active, so
    /// reservations lock in before any other pawn AI runs.
    /// </summary>
    public static class IngredientReservation
    {
        /// <summary>
        /// Counts available units of a material on the map that the given
        /// pawn can access.
        /// </summary>
        public static int CountAvailable(Map map, ThingDef thingDef, Pawn pawn)
        {
            int count = 0;
            foreach (Thing stack in map.listerThings.ThingsOfDef(thingDef))
            {
                if (CanPawnUseIngredient(stack, pawn))
                    count += stack.stackCount;
            }
            return count;
        }

        /// <summary>
        /// Result of <see cref="TryReserveIngredientsForJob"/>. Distinguishes
        /// success from the three failure modes so the caller can produce a
        /// specific log entry and player-facing message rather than a generic
        /// "click did nothing." The two failure modes that can realistically
        /// occur in production are <see cref="PlanInfeasible"/> and
        /// <see cref="ReservationConflict"/>; <see cref="NoActiveDriver"/>
        /// indicates an invariant violation (the dialog should only be open
        /// while our JobDriver is the active driver).
        /// </summary>
        public enum ReservationOutcome
        {
            Success,
            NoActiveDriver,
            PlanInfeasible,
            ReservationConflict,
        }

        /// <summary>
        /// Pairs an <see cref="ReservationOutcome"/> with the conflict detail
        /// for <see cref="ReservationOutcome.ReservationConflict"/>: which def
        /// + count failed to reserve, and (when discoverable) the pawn already
        /// holding the reservation. Lets callers surface a concrete
        /// "failed to reserve plasteel x75 (held by Steven)" message instead
        /// of a vague "materials unavailable." Conflict fields are default
        /// for non-conflict outcomes; ConflictReserver may be null on a real
        /// conflict if the existing reserver wasn't a pawn or wasn't reportable
        /// (e.g. mod-side reservations that don't expose a Pawn).
        /// </summary>
        public readonly struct ReservationResult
        {
            public readonly ReservationOutcome Outcome;
            public readonly ThingDef ConflictDef;
            public readonly int ConflictCount;
            public readonly Pawn ConflictReserver;

            public bool IsSuccess => Outcome == ReservationOutcome.Success;

            private ReservationResult(
                ReservationOutcome outcome, ThingDef def, int count, Pawn reserver)
            {
                Outcome = outcome;
                ConflictDef = def;
                ConflictCount = count;
                ConflictReserver = reserver;
            }

            public static ReservationResult Success() =>
                new ReservationResult(ReservationOutcome.Success, null, 0, null);
            public static ReservationResult NoActiveDriver() =>
                new ReservationResult(ReservationOutcome.NoActiveDriver, null, 0, null);
            public static ReservationResult PlanInfeasible() =>
                new ReservationResult(ReservationOutcome.PlanInfeasible, null, 0, null);
            public static ReservationResult Conflict(ThingDef def, int count, Pawn reserver) =>
                new ReservationResult(ReservationOutcome.ReservationConflict, def, count, reserver);
        }

        /// <summary>
        /// Builds a candidate pool, dispatches to the configured IHaulPlanner,
        /// reserves the chosen stacks against the active JobDriver's job, and
        /// populates job.targetQueueA / countQueue for the haul phase. Atomic:
        /// if planning fails or any reservation can't be acquired, releases
        /// anything it reserved and returns a failure result without mutating
        /// the job's queues. Pulls the active job from
        /// <c>pawn.jobs.curDriver.job</c> directly — the dialog only opens
        /// from inside the customize JobDriver's wait toil, so this is the
        /// authoritative source under the dialog's forcePause + absorbInput
        /// invariants.
        /// </summary>
        public static ReservationResult TryReserveIngredientsForJob(
            Pawn pawn, List<ThingDefCountClass> totalCost)
        {
            // Resolve the driver first — every downstream step depends on it,
            // and a missing driver is the only "invariant broke upstream" case
            // we can detect at this layer.
            var driver = pawn.jobs?.curDriver as JobDriver_CustomizeWeapon;
            if (driver == null)
                return ReservationResult.NoActiveDriver();

            Job job = driver.job;

            if (totalCost == null || totalCost.Count == 0)
                return ReservationResult.Success();

            // Demand: collapse possible duplicate ThingDef entries by summing.
            // TraitCostUtility.RunPipeline guarantees each entry is non-null
            // with a non-null thingDef and a positive count.
            var demand = new Dictionary<ThingDef, int>();
            foreach (ThingDefCountClass cost in totalCost)
            {
                demand.TryGetValue(cost.thingDef, out int existing);
                demand[cost.thingDef] = existing + cost.count;
            }

            HaulPlannerKind kind = UWU_Mod.Settings.haulPlannerKind;
            IntVec3 workbenchPos = job.GetTarget(TargetIndex.C).Cell;

            HaulPlan plan = AttemptPlan(pawn, demand, workbenchPos, kind);

            // Degradation ladder: Optimal -> Sweep -> Sequential. Each rung
            // runs only when the rung above produced no plan (returned null or
            // threw — AttemptPlan normalizes both to null), and rebuilds the
            // pool with its own planner's parameters. Common (benign) causes:
            // a planner's pool cap excludes stacks a lower rung's pool would
            // include, or Optimal's tractability guards trip. Each step is
            // logged at Message level so the dev console captures it for
            // diagnosis, but the player only sees a Messages.Message if the
            // final rung ALSO fails (PlanInfeasible below).
            if (plan == null && kind == HaulPlannerKind.Optimal)
            {
                Log.Message("[Unique Weapons Unbound] Configured haul planner "
                    + "(Optimal) returned no plan; retrying with Sweep.");
                plan = AttemptPlan(pawn, demand, workbenchPos, HaulPlannerKind.Sweep);
            }
            if (plan == null && kind != HaulPlannerKind.Sequential)
            {
                Log.Message("[Unique Weapons Unbound] Haul planning fell through "
                    + "to Sequential after higher-tier planners returned no plan.");
                plan = AttemptPlan(pawn, demand, workbenchPos, HaulPlannerKind.Sequential);
            }

            if (plan == null || !plan.IsValid)
                return ReservationResult.PlanInfeasible();

            return CommitPlanAtomic(pawn, job, driver, plan);
        }

        /// <summary>
        /// Builds the pool, request, and runs the configured planner. Returns
        /// null on failure (planner returned null, stub planner threw
        /// NotImplementedException, or planner threw any other exception).
        /// </summary>
        private static HaulPlan AttemptPlan(
            Pawn pawn, Dictionary<ThingDef, int> demand, IntVec3 workbenchPos, HaulPlannerKind kind)
        {
            IHaulPlanner planner = HaulPlannerFactory.Get(kind);

            Dictionary<ThingDef, List<HaulCandidate>> pool = BuildHaulPool(
                pawn, demand, planner.CandidatePoolMultiplier, planner.CandidatePoolCap);

            var request = new HaulPlanRequest
            {
                PawnPosition = pawn.Position,
                WorkbenchPosition = workbenchPos,
                CapacityKg = MassUtility.Capacity(pawn),
                // Gear AND inventory: anything already in the pawn's inventory
                // (meals, drugs, ammo) stays aboard across every haul trip, so
                // it occupies per-trip mass budget the whole time.
                CurrentEncumbranceKg = MassUtility.GearAndInventoryMass(pawn),
                Demand = demand,
                Pool = pool,
            };

            try
            {
                return planner.Plan(request);
            }
            catch (System.NotImplementedException)
            {
                Log.Warning($"[Unique Weapons Unbound] Selected haul planner "
                    + $"({kind}) is not implemented; falling back to Sequential.");
                return null;
            }
            catch (System.Exception ex)
            {
                // A planner crash would otherwise propagate up through the
                // Footer's confirm click handler and break the dialog frame
                // with no actionable diagnostic. Returning null lets the
                // caller's Sequential fallback (the bedrock path) try to
                // satisfy the same demand; the stack trace identifies the
                // offending planner for diagnosis.
                string suffix = kind == HaulPlannerKind.Sequential
                    ? "no further fallback available."
                    : "falling back to Sequential.";
                Log.Error($"[Unique Weapons Unbound] Haul planner ({kind}) threw "
                    + $"during Plan(); {suffix} Exception: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Reserves every unique Thing in the plan atomically and commits the
        /// plan to the job's haul queues plus the driver's parallel destination
        /// and trip-boundary lists. Same Thing may appear in multiple pickups
        /// (split across CarryTracker + Inventory or across trips); we reserve
        /// it once, and the driver re-reserves before subsequent pickups since
        /// vanilla's Toils_Haul.StartCarryThing auto-releases when the per-
        /// pickup count is satisfied. On reservation failure the returned
        /// result carries the failed pickup's def + count so the caller can
        /// surface a concrete "failed to reserve {def} x{count}" message.
        /// </summary>
        private static ReservationResult CommitPlanAtomic(
            Pawn pawn, Job job, JobDriver_CustomizeWeapon driver, HaulPlan plan)
        {
            var reservedThings = new HashSet<Thing>();
            var queueA = new List<LocalTargetInfo>();
            var countQueue = new List<int>();
            var destinations = new List<int>();
            var lastInTripFlags = new List<bool>();

            foreach (HaulTrip trip in plan.Trips)
            {
                if (trip?.Pickups == null || trip.Pickups.Count == 0)
                    continue;

                int lastValidIdx = -1;
                for (int i = trip.Pickups.Count - 1; i >= 0; i--)
                {
                    HaulPickup p = trip.Pickups[i];
                    if (p.Thing != null && p.Count > 0) { lastValidIdx = i; break; }
                }
                if (lastValidIdx < 0)
                    continue;

                for (int i = 0; i < trip.Pickups.Count; i++)
                {
                    HaulPickup pickup = trip.Pickups[i];
                    if (pickup.Thing == null || pickup.Count <= 0)
                        continue;

                    if (reservedThings.Add(pickup.Thing))
                    {
                        if (!pawn.Reserve(pickup.Thing, job, 1, -1, null, errorOnFailed: false))
                        {
                            // Surface the current reserver (if any) as the most
                            // useful piece of forensic data — in single-player
                            // it's almost always another job on the same pawn
                            // (a stale hold from an earlier interrupt); in
                            // multiplayer it could be a peer's pawn entirely.
                            // Looked up once and shared with both the log line
                            // and the returned result so they agree.
                            Pawn reserver = pawn.Map.reservationManager
                                .FirstRespectedReserver(pickup.Thing, pawn);
                            string reserverInfo = reserver != null
                                ? " (held by " + reserver.LabelShortCap + ")"
                                : "";
                            Log.Warning("[Unique Weapons Unbound] Could not reserve "
                                + pickup.Thing.LabelShortCap + " x" + pickup.Count
                                + " for customization haul" + reserverInfo + ".");

                            foreach (Thing t in reservedThings)
                                pawn.Map.reservationManager.Release(t, pawn, job);
                            return ReservationResult.Conflict(
                                pickup.Thing.def, pickup.Count, reserver);
                        }
                    }

                    queueA.Add(pickup.Thing);
                    countQueue.Add(pickup.Count);
                    destinations.Add((int)pickup.Destination);
                    lastInTripFlags.Add(i == lastValidIdx);
                }
            }

            job.targetQueueA = queueA;
            job.countQueue = countQueue;
            driver.SetHaulPickupMetadata(plan.ExecutionStrategy, destinations, lastInTripFlags);
            return ReservationResult.Success();
        }

        /// <summary>
        /// Determines whether a pawn can use the given thing as an ingredient.
        /// Checks: spawned, not flagged forbidden by the player faction, not
        /// forbidden to the pawn (allowed-area), reservable by the pawn, and
        /// reachable. The faction-level forbidden check is explicit because
        /// IsForbidden(pawn) short-circuits to false for drafted/slave/host-faction
        /// pawns via CaresAboutForbidden, which would otherwise allow forbidden
        /// stacks to be hauled. Customization is a direct player order, so the
        /// player's red-X must always be respected regardless of pawn state.
        /// </summary>
        private static bool CanPawnUseIngredient(Thing thing, Pawn pawn)
        {
            return thing.Spawned
                && !thing.IsForbidden(Faction.OfPlayer)
                && !thing.IsForbidden(pawn)
                && pawn.CanReserve(thing)
                && pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly);
        }

        /// <summary>
        /// Gathers candidate stacks per demanded def for the planner. Each
        /// candidate has already passed CanPawnUseIngredient. The pool is
        /// sized to <paramref name="multiplier"/> times demand, capped per def
        /// at <paramref name="capPerDef"/>; richer pools give planners more
        /// sourcing flexibility at the cost of build time.
        ///
        /// Sort key for "which stacks to keep when capping" is squared
        /// distance from the pawn, matching the existing one-stack-per-trip
        /// behavior. Planners that prefer a workbench-anchored sort can
        /// re-sort the pool internally without losing candidates.
        /// </summary>
        private static Dictionary<ThingDef, List<HaulCandidate>> BuildHaulPool(
            Pawn pawn, Dictionary<ThingDef, int> demand, float multiplier, int capPerDef)
        {
            var pool = new Dictionary<ThingDef, List<HaulCandidate>>();
            IntVec3 origin = pawn.Position;

            foreach (KeyValuePair<ThingDef, int> entry in demand)
            {
                ThingDef def = entry.Key;
                int needed = entry.Value;
                int targetCount = Mathf.CeilToInt(needed * multiplier);

                List<Thing> stacks = pawn.Map.listerThings.ThingsOfDef(def)
                    .Where(t => CanPawnUseIngredient(t, pawn))
                    .OrderBy(t => (t.Position - origin).LengthHorizontalSquared)
                    .ToList();

                var candidates = new List<HaulCandidate>(stacks.Count);
                int cumulative = 0;
                foreach (Thing stack in stacks)
                {
                    if (candidates.Count >= capPerDef)
                        break;
                    candidates.Add(new HaulCandidate
                    {
                        Thing = stack,
                        Position = stack.Position,
                        AvailableCount = stack.stackCount,
                        MassPerUnit = stack.GetStatValue(StatDefOf.Mass),
                    });
                    cumulative += stack.stackCount;
                    // Stop once we have enough cumulative count to cover the
                    // pool target. The last candidate may overshoot; that's
                    // fine — the planner can partial-take it.
                    if (cumulative >= targetCount)
                        break;
                }

                pool[def] = candidates;
            }

            return pool;
        }
    }
}

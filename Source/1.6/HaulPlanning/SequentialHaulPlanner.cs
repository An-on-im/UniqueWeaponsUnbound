using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /// <summary>
    /// One-stack-per-trip planner — the algorithm Unique Weapons Unbound has
    /// shipped with since the customization flow was added. For each demanded
    /// def it sorts candidate stacks by squared horizontal distance from the
    /// pawn and takes them in order until demand is met, emitting each picked
    /// stack as its own single-pickup trip. No batching, no encumbrance math,
    /// no awareness of the workbench position.
    ///
    /// Behaviorally identical to the pre-refactor inline implementation in
    /// WeaponModificationUtility.TryReserveIngredientsForJob — kept as the
    /// default so existing players see no change unless they opt in to one of
    /// the experimental planners. Pool sizing is exact-fit (multiplier 1.0,
    /// no cap) to preserve that equivalence: a richer pool would let this
    /// planner pick different (still-valid) stacks than today's behavior.
    /// </summary>
    public class SequentialHaulPlanner : IHaulPlanner
    {
        public float CandidatePoolMultiplier => 1.0f;

        public int CandidatePoolCap => int.MaxValue;

        // Stack-level pool, untouched by the grouped-pool contract — keeps
        // this planner's vanilla-equivalence intact.
        public bool GroupPoolBySlotGroup => false;

        public HaulPlan Plan(HaulPlanRequest request)
        {
            if (request.Demand == null || request.Demand.Count == 0)
                return new HaulPlan
                {
                    Trips = new List<HaulTrip>(),
                    ExecutionStrategy = HaulPlanExecutionStrategy.VanillaCarryOnly,
                };

            var trips = new List<HaulTrip>();

            foreach (KeyValuePair<ThingDef, int> demand in request.Demand)
            {
                int remaining = demand.Value;
                if (remaining <= 0)
                    continue;

                if (!request.Pool.TryGetValue(demand.Key, out List<HaulCandidate> candidates)
                    || candidates == null
                    || candidates.Count == 0)
                {
                    return null;
                }

                // Sort by squared distance from the pawn — matches the original
                // implementation's behavior. Sorting in-place is fine; the pool
                // is built fresh per planning attempt.
                IntVec3 origin = request.PawnPosition;
                candidates.Sort((a, b) =>
                    (a.Position - origin).LengthHorizontalSquared
                        .CompareTo((b.Position - origin).LengthHorizontalSquared));

                foreach (HaulCandidate candidate in candidates)
                {
                    if (remaining <= 0)
                        break;

                    int take = Mathf.Min(remaining, candidate.AvailableCount);
                    if (take <= 0)
                        continue;

                    trips.Add(new HaulTrip
                    {
                        Pickups = new List<HaulPickup>
                        {
                            new HaulPickup { Thing = candidate.Thing, Count = take }
                        }
                    });
                    remaining -= take;
                }

                if (remaining > 0)
                    return null;
            }

            return new HaulPlan
            {
                Trips = trips,
                ExecutionStrategy = HaulPlanExecutionStrategy.VanillaCarryOnly,
            };
        }
    }
}

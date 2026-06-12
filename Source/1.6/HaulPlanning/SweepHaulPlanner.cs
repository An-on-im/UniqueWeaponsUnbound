using System;
using System.Collections.Generic;
using UniqueWeaponsUnbound.HaulPlanning.Internal;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /// <summary>
    /// Mid-tier planner: clusters ingredient stacks geographically using the
    /// Gillett &amp; Miller (1974) sweep algorithm anchored at the workbench,
    /// hybrid bin-packs the sweep-ordered list into trips (one carry-tracker
    /// pickup + mass-budgeted inventory pickups per trip), repairs partition
    /// quality with a 2-opt swap pass on inventory pickups across adjacent
    /// trips, then solves the in-trip TSP exactly with Held-Karp DP. Manhattan
    /// distance throughout (matches RimWorld's grid pathing better than
    /// Euclidean and preserves triangle inequality).
    ///
    /// Hybrid carry model: the heaviest pickup in each trip is routed via the
    /// pawn's carry tracker (volume-bound only — see Pawn_CarryTracker.
    /// MaxStackSpaceEver), letting that mass bypass the inventory budget at no
    /// encumbrance cost (MassUtility.GearAndInventoryMass excludes the carry
    /// tracker). Remaining trip pickups go into inventory under a
    /// spareCap * 0.95 mass cap. Same Thing may legitimately appear as both a
    /// CarryTracker pickup and an Inventory pickup within the same trip when
    /// its required count exceeds the carry-tracker volume budget — common
    /// with stack-size mods.
    /// </summary>
    public class SweepHaulPlanner : IHaulPlanner
    {
        public float CandidatePoolMultiplier => 1.5f;

        public int CandidatePoolCap => 6;

        // Stack-level pool — grouping is the Optimal planner's contract.
        public bool GroupPoolBySlotGroup => false;

        private const int MaxRepairRounds = 3;

        public HaulPlan Plan(HaulPlanRequest request)
        {
            if (request.Demand == null || request.Demand.Count == 0)
                return new HaulPlan
                {
                    Trips = new List<HaulTrip>(),
                    ExecutionStrategy = HaulPlanExecutionStrategy.UwuCarryInventoryHybrid,
                };

            if (request.Pool == null)
                return null;

            float invBudget = (request.CapacityKg - request.CurrentEncumbranceKg) * HaulMath.CapacityFactor;
            if (invBudget <= 0f)
                return null;

            IntVec3 wb = request.WorkbenchPosition;

            List<PlanPickup> selected = SourceByAngle(request.Demand, request.Pool, wb);
            if (selected == null)
                return null;
            if (selected.Count == 0)
                return new HaulPlan
                {
                    Trips = new List<HaulTrip>(),
                    ExecutionStrategy = HaulPlanExecutionStrategy.UwuCarryInventoryHybrid,
                };

            List<List<PlanPickup>> trips = HybridBinPack(selected, wb, invBudget, request.CapacityKg);
            if (trips == null)
                return null;

            Repair(trips, wb, invBudget);

            foreach (List<PlanPickup> trip in trips)
                SequenceTrip(trip, wb);

            var output = new List<HaulTrip>(trips.Count);
            foreach (List<PlanPickup> trip in trips)
            {
                var pickups = new List<HaulPickup>(trip.Count);
                foreach (PlanPickup p in trip)
                {
                    pickups.Add(new HaulPickup
                    {
                        Thing = p.Thing,
                        Count = p.Count,
                        Destination = p.Destination,
                    });
                }
                output.Add(new HaulTrip { Pickups = pickups });
            }
            return new HaulPlan
            {
                Trips = output,
                ExecutionStrategy = HaulPlanExecutionStrategy.UwuCarryInventoryHybrid,
            };
        }

        // Phase 1: per def, sort the def's pool by polar angle around the
        // workbench and take in order until cumulative count meets demand.
        // Partial-take the last stack to land exactly on demand.
        private static List<PlanPickup> SourceByAngle(
            Dictionary<ThingDef, int> demand,
            Dictionary<ThingDef, List<HaulCandidate>> pool,
            IntVec3 wb)
        {
            var selected = new List<PlanPickup>();

            foreach (KeyValuePair<ThingDef, int> entry in demand)
            {
                int needed = entry.Value;
                if (needed <= 0)
                    continue;

                if (!pool.TryGetValue(entry.Key, out List<HaulCandidate> candidates)
                    || candidates == null
                    || candidates.Count == 0)
                {
                    return null;
                }

                SortByAngle(candidates, wb);

                int cumulative = 0;
                foreach (HaulCandidate c in candidates)
                {
                    if (cumulative >= needed)
                        break;
                    if (c.AvailableCount <= 0)
                        continue;
                    int take = Mathf.Min(c.AvailableCount, needed - cumulative);
                    selected.Add(new PlanPickup
                    {
                        Thing = c.Thing,
                        Position = c.Position,
                        Count = take,
                        MassPerUnit = c.MassPerUnit,
                        Destination = PickupDestination.Inventory,
                    });
                    cumulative += take;
                }

                if (cumulative < needed)
                    return null;
            }

            return selected;
        }

        // Phase 2: hybrid bin-pack. Per trip:
        //   Step 0 — Pre-promote: pick the heaviest-by-total-mass pickup with
        //            remaining demand and route it via the carry tracker (free
        //            on the inventory mass budget; volume-capped by the def's
        //            MaxStackSpaceEver). Volume overflow becomes an Inventory
        //            pickup of the same Thing in the same trip.
        //   Step A — Pack remaining pickups in polar-angle order into
        //            Inventory under a spareCap*0.95 mass cap.
        //
        // Pre-promoting BEFORE inventory packing matters: an inventory-first
        // strategy can split the heaviest stack across trips when its count
        // exceeds the inv budget but would have fit entirely under the carry
        // tracker's (much larger) volume budget — wasting a trip.
        private static List<List<PlanPickup>> HybridBinPack(
            List<PlanPickup> selected, IntVec3 wb, float invBudget, float capacityKg)
        {
            SortByRotatedAngle(selected, wb);

            var pending = new List<PlanPickup>(selected);
            var trips = new List<List<PlanPickup>>();

            while (HasRemaining(pending))
            {
                var trip = new List<PlanPickup>();
                float invUsed = 0f;

                // Step 0: pre-promote heaviest to carry tracker.
                int heaviestIdx = ArgmaxByTotalMass(pending);
                if (heaviestIdx >= 0)
                {
                    PlanPickup p = pending[heaviestIdx];
                    int ctVolume = HaulMath.MaxStackSpaceEver(p.Thing.def, capacityKg);
                    if (ctVolume > 0)
                    {
                        int ctTake = Mathf.Min(p.Count, ctVolume);
                        trip.Add(new PlanPickup
                        {
                            Thing = p.Thing,
                            Position = p.Position,
                            Count = ctTake,
                            MassPerUnit = p.MassPerUnit,
                            Destination = PickupDestination.CarryTracker,
                        });
                        p.Count -= ctTake;
                        pending[heaviestIdx] = p;

                        // Volume overflow rides as Inventory in the same trip if
                        // the budget can absorb it; otherwise carries forward.
                        if (p.Count > 0 && p.MassPerUnit > 0f)
                        {
                            int invMax = Mathf.FloorToInt((invBudget - invUsed + HaulMath.MassEpsilon) / p.MassPerUnit);
                            if (invMax > 0)
                            {
                                int invTake = Mathf.Min(p.Count, invMax);
                                trip.Add(new PlanPickup
                                {
                                    Thing = p.Thing,
                                    Position = p.Position,
                                    Count = invTake,
                                    MassPerUnit = p.MassPerUnit,
                                    Destination = PickupDestination.Inventory,
                                });
                                invUsed += invTake * p.MassPerUnit;
                                p.Count -= invTake;
                                pending[heaviestIdx] = p;
                            }
                        }
                    }
                }

                // Step A: pack remaining pending into Inventory in polar order.
                for (int i = 0; i < pending.Count; i++)
                {
                    PlanPickup p = pending[i];
                    if (p.Count == 0) continue;
                    if (p.MassPerUnit <= 0f)
                    {
                        // Massless ingredient — take everything; budget unaffected.
                        trip.Add(new PlanPickup
                        {
                            Thing = p.Thing,
                            Position = p.Position,
                            Count = p.Count,
                            MassPerUnit = 0f,
                            Destination = PickupDestination.Inventory,
                        });
                        p.Count = 0;
                        pending[i] = p;
                        continue;
                    }
                    int invMax = Mathf.FloorToInt((invBudget - invUsed + HaulMath.MassEpsilon) / p.MassPerUnit);
                    if (invMax <= 0) continue;
                    int take = Mathf.Min(p.Count, invMax);
                    trip.Add(new PlanPickup
                    {
                        Thing = p.Thing,
                        Position = p.Position,
                        Count = take,
                        MassPerUnit = p.MassPerUnit,
                        Destination = PickupDestination.Inventory,
                    });
                    invUsed += take * p.MassPerUnit;
                    p.Count -= take;
                    pending[i] = p;
                }

                if (trip.Count == 0)
                {
                    // No pickup advanced — must be a def with ctVolume=0 and
                    // unit-mass exceeding the inv budget. Genuinely unfittable;
                    // upstream caller falls back to Sequential.
                    return null;
                }

                trips.Add(trip);
            }

            return trips;
        }

        // Phase 2.5: local-search repair pass on Inventory pickups across
        // adjacent trip pairs. CT pickups are pinned per trip (each trip's
        // heaviest stack at bin-pack time), so they're excluded from both
        // primitives. Two primitives, applied per round until no change:
        //   - Swap: 2-opt swap of one Inventory pickup in trip t with one in
        //     trip t+1. Repairs partitions where pure-angle sweep interleaved
        //     near and far stacks along the same ray.
        //   - Move: relocate an Inventory pickup from one trip to its neighbour
        //     when the neighbour has spare mass budget. Catches the failure
        //     mode where the receiving trip has no Inventory pickup to swap
        //     against (common — Step 0 promotes the heaviest leftover into CT,
        //     leaving CT-only trips that swap-only cannot reach).
        private static void Repair(
            List<List<PlanPickup>> trips, IntVec3 wb, float invBudget)
        {
            if (trips.Count < 2) return;

            float[] tripInvMass = new float[trips.Count];
            int[] tripCost = new int[trips.Count];
            for (int i = 0; i < trips.Count; i++)
            {
                tripInvMass[i] = SumInventoryMass(trips[i]);
                tripCost[i] = HeldKarpCost(trips[i], wb);
            }

            for (int round = 0; round < MaxRepairRounds; round++)
            {
                bool improved = false;
                for (int t = 0; t + 1 < trips.Count; t++)
                {
                    improved |= TrySwapInv(trips, t, t + 1, tripInvMass, tripCost, wb, invBudget);
                    improved |= TryMoveInv(trips, t, t + 1, tripInvMass, tripCost, wb, invBudget);
                    improved |= TryMoveInv(trips, t + 1, t, tripInvMass, tripCost, wb, invBudget);
                }
                if (!improved) break;
            }
        }

        private static bool TrySwapInv(
            List<List<PlanPickup>> trips, int t1, int t2,
            float[] tripInvMass, int[] tripCost, IntVec3 wb, float invBudget)
        {
            List<PlanPickup> a = trips[t1];
            List<PlanPickup> b = trips[t2];
            bool improved = false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Destination != PickupDestination.Inventory) continue;
                for (int j = 0; j < b.Count; j++)
                {
                    if (b[j].Destination != PickupDestination.Inventory) continue;

                    PlanPickup pa = a[i];
                    PlanPickup pb = b[j];
                    float massA = pa.Count * pa.MassPerUnit;
                    float massB = pb.Count * pb.MassPerUnit;
                    float newMassA = tripInvMass[t1] - massA + massB;
                    float newMassB = tripInvMass[t2] - massB + massA;
                    if (newMassA > invBudget + HaulMath.MassEpsilon) continue;
                    if (newMassB > invBudget + HaulMath.MassEpsilon) continue;

                    a[i] = pb;
                    b[j] = pa;
                    int newCostA = HeldKarpCost(a, wb);
                    int newCostB = HeldKarpCost(b, wb);
                    if (newCostA + newCostB < tripCost[t1] + tripCost[t2])
                    {
                        tripInvMass[t1] = newMassA;
                        tripInvMass[t2] = newMassB;
                        tripCost[t1] = newCostA;
                        tripCost[t2] = newCostB;
                        improved = true;
                    }
                    else
                    {
                        a[i] = pa;
                        b[j] = pb;
                    }
                }
            }
            return improved;
        }

        // Move a single Inventory pickup from src trip to dst trip when dst
        // has spare mass budget and the move strictly improves total Held-Karp
        // cost. Skips the case where src would be left empty — trip collapse
        // is a separate operation; here we preserve the trip count.
        private static bool TryMoveInv(
            List<List<PlanPickup>> trips, int srcIdx, int dstIdx,
            float[] tripInvMass, int[] tripCost, IntVec3 wb, float invBudget)
        {
            List<PlanPickup> src = trips[srcIdx];
            List<PlanPickup> dst = trips[dstIdx];
            bool improved = false;

            // Reverse iteration so RemoveAt doesn't disturb earlier indexes.
            for (int i = src.Count - 1; i >= 0; i--)
            {
                PlanPickup p = src[i];
                if (p.Destination != PickupDestination.Inventory) continue;
                if (src.Count == 1) continue;

                float pMass = p.Count * p.MassPerUnit;
                float newDstMass = tripInvMass[dstIdx] + pMass;
                if (newDstMass > invBudget + HaulMath.MassEpsilon) continue;

                src.RemoveAt(i);
                dst.Add(p);
                int newCostSrc = HeldKarpCost(src, wb);
                int newCostDst = HeldKarpCost(dst, wb);
                if (newCostSrc + newCostDst < tripCost[srcIdx] + tripCost[dstIdx])
                {
                    tripInvMass[srcIdx] -= pMass;
                    tripInvMass[dstIdx] = newDstMass;
                    tripCost[srcIdx] = newCostSrc;
                    tripCost[dstIdx] = newCostDst;
                    improved = true;
                }
                else
                {
                    dst.RemoveAt(dst.Count - 1);
                    src.Insert(i, p);
                }
            }
            return improved;
        }

        // Phase 3: reorder pickups within a trip via the shared Held-Karp
        // solver. Destination doesn't affect the geographic optimization —
        // the pawn visits each pickup's position once regardless of
        // carry-tracker vs inventory, and the solver dedupes same-cell
        // pickups (e.g. a CT/inventory split of one stack) into one node.
        private static void SequenceTrip(List<PlanPickup> trip, IntVec3 wb)
        {
            int k = trip.Count;
            if (k <= 2) return;

            int[] order = HeldKarp.Order(TripPositions(trip), wb);
            var reordered = new List<PlanPickup>(k);
            for (int s = 0; s < k; s++)
                reordered.Add(trip[order[s]]);
            trip.Clear();
            trip.AddRange(reordered);
        }

        // Minimum tour cost (workbench → all pickups → workbench) via the
        // shared solver; used by Repair to compare candidate partitions.
        private static int HeldKarpCost(List<PlanPickup> trip, IntVec3 wb)
        {
            if (trip.Count == 0) return 0;
            return HeldKarp.Cost(TripPositions(trip), wb);
        }

        private static List<IntVec3> TripPositions(List<PlanPickup> trip)
        {
            var positions = new List<IntVec3>(trip.Count);
            foreach (PlanPickup p in trip)
                positions.Add(p.Position);
            return positions;
        }

        private static void SortByAngle(List<HaulCandidate> candidates, IntVec3 wb)
        {
            candidates.Sort((a, b) =>
            {
                float angleA = Mathf.Atan2(a.Position.z - wb.z, a.Position.x - wb.x);
                float angleB = Mathf.Atan2(b.Position.z - wb.z, b.Position.x - wb.x);
                int c = angleA.CompareTo(angleB);
                if (c != 0) return c;
                int distA = HaulMath.ManhattanDist(a.Position, wb);
                int distB = HaulMath.ManhattanDist(b.Position, wb);
                return distA.CompareTo(distB);
            });
        }

        private static void SortByAngle(List<PlanPickup> pickups, IntVec3 wb)
        {
            pickups.Sort((a, b) =>
            {
                float angleA = Mathf.Atan2(a.Position.z - wb.z, a.Position.x - wb.x);
                float angleB = Mathf.Atan2(b.Position.z - wb.z, b.Position.x - wb.x);
                int c = angleA.CompareTo(angleB);
                if (c != 0) return c;
                int distA = HaulMath.ManhattanDist(a.Position, wb);
                int distB = HaulMath.ManhattanDist(b.Position, wb);
                return distA.CompareTo(distB);
            });
        }

        // Sweep-with-rotation: same angle ordering, but rotated so the largest
        // empty arc straddles the wrap-point. Atan2 returns angles in (-π, +π],
        // and a plain ascending sort splits any natural cluster that crosses
        // due-west across the start and end of the list. Rotating the start to
        // the far edge of the largest gap keeps every cluster contiguous in
        // the bin-pack order. When the wrap gap is itself the largest (the
        // common case), no rotation is applied and the result matches plain
        // SortByAngle.
        private static void SortByRotatedAngle(List<PlanPickup> pickups, IntVec3 wb)
        {
            int n = pickups.Count;
            if (n <= 1) return;

            float[] sorted = new float[n];
            for (int i = 0; i < n; i++)
                sorted[i] = Mathf.Atan2(pickups[i].Position.z - wb.z,
                                        pickups[i].Position.x - wb.x);
            Array.Sort(sorted);

            float bestGap = sorted[0] + 2f * Mathf.PI - sorted[n - 1];
            float startAngle = sorted[0];
            for (int i = 1; i < n; i++)
            {
                float g = sorted[i] - sorted[i - 1];
                if (g > bestGap) { bestGap = g; startAngle = sorted[i]; }
            }

            float start = startAngle;
            pickups.Sort((a, b) =>
            {
                float angA = Mathf.Atan2(a.Position.z - wb.z, a.Position.x - wb.x) - start;
                if (angA < 0f) angA += 2f * Mathf.PI;
                float angB = Mathf.Atan2(b.Position.z - wb.z, b.Position.x - wb.x) - start;
                if (angB < 0f) angB += 2f * Mathf.PI;
                int c = angA.CompareTo(angB);
                if (c != 0) return c;
                return HaulMath.ManhattanDist(a.Position, wb).CompareTo(HaulMath.ManhattanDist(b.Position, wb));
            });
        }

        private static int ArgmaxByTotalMass(List<PlanPickup> pickups)
        {
            int best = -1;
            float bestMass = -1f;
            for (int i = 0; i < pickups.Count; i++)
            {
                if (pickups[i].Count <= 0) continue;
                float m = pickups[i].Count * pickups[i].MassPerUnit;
                if (m > bestMass) { bestMass = m; best = i; }
            }
            return best;
        }

        private static bool HasRemaining(List<PlanPickup> pickups)
        {
            for (int i = 0; i < pickups.Count; i++)
                if (pickups[i].Count > 0) return true;
            return false;
        }

        private static float SumInventoryMass(List<PlanPickup> trip)
        {
            float m = 0f;
            foreach (PlanPickup p in trip)
            {
                if (p.Destination == PickupDestination.Inventory)
                    m += p.Count * p.MassPerUnit;
            }
            return m;
        }

        private struct PlanPickup
        {
            public Thing Thing;
            public IntVec3 Position;
            public int Count;
            public float MassPerUnit;
            public PickupDestination Destination;
        }
    }
}

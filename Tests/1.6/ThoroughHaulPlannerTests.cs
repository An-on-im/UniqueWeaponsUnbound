using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniqueWeaponsUnbound.HaulPlanning;
using UniqueWeaponsUnbound.HaulPlanning.Internal;
using Verse;
using Xunit;

namespace UniqueWeaponsUnbound.Tests
{
    /// <summary>
    /// Tests for ThoroughHaulPlanner per the spec's TESTING NOTES: contract
    /// invariants, guard behavior, grouping semantics, determinism, an
    /// exhaustive brute-force oracle on small instances (including plans that
    /// split a stack across trips), a comparative property against Sweep, and
    /// op-count performance asserts (wall-clock is flaky on the CI runner).
    /// </summary>
    public class ThoroughHaulPlannerTests
    {
        private static readonly ThoroughHaulPlanner Planner = new ThoroughHaulPlanner();

        static ThoroughHaulPlannerTests()
        {
            // Verse.Log isn't usable under the bare xUnit runner; guard-trip
            // tests would otherwise crash on the log call instead of
            // asserting the null contract.
            ThoroughHaulPlanner.EmitGuardLogs = false;
        }

        // ------------------------------------------------------------------
        // Fixture plumbing
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds planner inputs while remembering each created Thing's
        /// position, availability, and unit mass — HaulPickup carries only
        /// the Thing, so cost and invariant checks need this side table.
        /// </summary>
        private sealed class Fixture
        {
            public readonly Dictionary<ThingDef, int> Demand = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, List<HaulCandidate>> Pool =
                new Dictionary<ThingDef, List<HaulCandidate>>();
            public readonly Dictionary<Thing, IntVec3> PositionOf = new Dictionary<Thing, IntVec3>();
            public readonly Dictionary<Thing, int> AvailOf = new Dictionary<Thing, int>();
            public readonly Dictionary<Thing, float> UnitMassOf = new Dictionary<Thing, float>();
            public float CapacityKg = 75f;
            public float EncumbranceKg = 0f;
            public IntVec3 Wb = new IntVec3(10, 0, 10);

            public Thing Add(ThingDef def, IntVec3 pos, int count, float massPerUnit, int groupId = -1)
            {
                HaulCandidate c = TestHelpers.MakeCandidate(def, pos, count, massPerUnit, groupId);
                if (!Pool.TryGetValue(def, out List<HaulCandidate> list))
                {
                    list = new List<HaulCandidate>();
                    Pool[def] = list;
                }
                list.Add(c);
                PositionOf[c.Thing] = pos;
                AvailOf[c.Thing] = count;
                UnitMassOf[c.Thing] = massPerUnit;
                return c.Thing;
            }

            public HaulPlanRequest Request()
            {
                return TestHelpers.MakeRequest(
                    capacityKg: CapacityKg,
                    currentEncumbranceKg: EncumbranceKg,
                    workbenchPos: Wb,
                    demand: Demand,
                    pool: Pool);
            }

            public float InventoryBudget => (CapacityKg - EncumbranceKg) * 0.95f;
        }

        private static int Dist(IntVec3 a, IntVec3 b)
        {
            return Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z);
        }

        // Optimal closed-tour cost by brute-force permutation — independent
        // of the planner's Held-Karp implementation.
        private static int BruteTour(List<IntVec3> pts, IntVec3 wb)
        {
            if (pts.Count == 0) return 0;
            var used = new bool[pts.Count];
            int best = int.MaxValue;

            void Recurse(IntVec3 cur, int visited, int dist)
            {
                if (dist >= best) return;
                if (visited == pts.Count)
                {
                    int total = dist + Dist(cur, wb);
                    if (total < best) best = total;
                    return;
                }
                for (int i = 0; i < pts.Count; i++)
                {
                    if (used[i]) continue;
                    used[i] = true;
                    Recurse(pts[i], visited + 1, dist + Dist(cur, pts[i]));
                    used[i] = false;
                }
            }

            Recurse(wb, 0, 0);
            return best;
        }

        private static int PlanCost(Fixture fx, HaulPlan plan)
        {
            int total = 0;
            foreach (HaulTrip trip in plan.Trips)
            {
                var unique = new List<IntVec3>();
                foreach (HaulPickup p in trip.Pickups)
                {
                    IntVec3 pos = fx.PositionOf[p.Thing];
                    if (!unique.Contains(pos)) unique.Add(pos);
                }
                total += BruteTour(unique, fx.Wb);
            }
            return total;
        }

        // The planner's output contract: totals exactly equal demand, takes
        // within snapshot availability, at most one CarryTracker pickup per
        // trip, positive counts, per-trip inventory mass within budget, and
        // the hybrid execution strategy.
        private static void AssertInvariants(Fixture fx, HaulPlan plan)
        {
            Assert.NotNull(plan);
            Assert.True(plan.IsValid);
            Assert.Equal(HaulPlanExecutionStrategy.UwuCarryInventoryHybrid, plan.ExecutionStrategy);

            var totals = new Dictionary<ThingDef, int>();
            var perThing = new Dictionary<Thing, int>();
            foreach (HaulTrip trip in plan.Trips)
            {
                Assert.NotNull(trip.Pickups);
                Assert.NotEmpty(trip.Pickups);
                int ctCount = 0;
                float invMass = 0f;
                foreach (HaulPickup p in trip.Pickups)
                {
                    Assert.True(p.Count > 0, "pickup with non-positive count");
                    totals.TryGetValue(p.Thing.def, out int t);
                    totals[p.Thing.def] = t + p.Count;
                    perThing.TryGetValue(p.Thing, out int pt);
                    perThing[p.Thing] = pt + p.Count;
                    if (p.Destination == PickupDestination.CarryTracker) ctCount++;
                    else invMass += p.Count * fx.UnitMassOf[p.Thing];
                }
                Assert.True(ctCount <= 1, $"trip has {ctCount} CarryTracker pickups");
                Assert.True(invMass <= fx.InventoryBudget + 0.01f,
                    $"trip inventory mass {invMass} exceeds budget {fx.InventoryBudget}");
            }

            foreach (KeyValuePair<ThingDef, int> entry in fx.Demand)
            {
                totals.TryGetValue(entry.Key, out int got);
                Assert.True(entry.Value == got,
                    $"demand {entry.Key.defName}={entry.Value} but plan totals {got}");
            }
            foreach (KeyValuePair<Thing, int> entry in perThing)
            {
                Assert.True(entry.Value <= fx.AvailOf[entry.Key],
                    $"take {entry.Value} exceeds availability {fx.AvailOf[entry.Key]}");
            }
        }

        private static string Describe(Fixture fx, HaulPlan plan)
        {
            var sb = new StringBuilder();
            foreach (HaulTrip trip in plan.Trips)
            {
                sb.Append("T[");
                foreach (HaulPickup p in trip.Pickups)
                {
                    IntVec3 pos = fx.PositionOf[p.Thing];
                    sb.Append(p.Thing.def.defName).Append('@')
                      .Append(pos.x).Append(',').Append(pos.z)
                      .Append('x').Append(p.Count).Append(':')
                      .Append((int)p.Destination).Append(';');
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Edge cases & defensive behavior
        // ------------------------------------------------------------------

        [Fact]
        public void EmptyDemand_ReturnsEmptyHybridPlan()
        {
            var plan = Planner.Plan(TestHelpers.MakeRequest());
            Assert.NotNull(plan);
            Assert.True(plan.IsValid);
            Assert.Empty(plan.Trips);
            Assert.Equal(HaulPlanExecutionStrategy.UwuCarryInventoryHybrid, plan.ExecutionStrategy);
        }

        [Fact]
        public void MissingDefInPool_ReturnsNull()
        {
            var steel = TestHelpers.MakeDef("OptTestSteelMissing");
            var request = TestHelpers.MakeRequest(
                demand: new Dictionary<ThingDef, int> { [steel] = 10 });
            Assert.Null(Planner.Plan(request));
        }

        [Fact]
        public void InsufficientAvailability_ReturnsNull()
        {
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestSteelShort");
            fx.Demand[steel] = 100;
            fx.Add(steel, new IntVec3(5, 0, 5), 60, 1f);
            Assert.Null(Planner.Plan(fx.Request()));
        }

        [Fact]
        public void NonPositiveBudget_ReturnsNull()
        {
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestSteelNoBudget");
            fx.Demand[steel] = 10;
            fx.Add(steel, new IntVec3(5, 0, 5), 50, 1f);
            fx.EncumbranceKg = fx.CapacityKg; // budget == 0
            Assert.Null(Planner.Plan(fx.Request()));
        }

        // ------------------------------------------------------------------
        // Contract invariants & basic shapes
        // ------------------------------------------------------------------

        [Fact]
        public void SingleStack_SingleTrip_CarryTrackerPickup()
        {
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestSteelSingle");
            fx.Demand[steel] = 30;
            fx.Add(steel, new IntVec3(5, 0, 5), 50, 1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            Assert.Single(plan.Trips);
            var pickup = Assert.Single(plan.Trips[0].Pickups);
            Assert.Equal(30, pickup.Count);
            Assert.Equal(PickupDestination.CarryTracker, pickup.Destination);
        }

        [Fact]
        public void ContractInvariants_MultiDefMultiTrip()
        {
            var fx = new Fixture();
            fx.CapacityKg = 35f;
            var steel = TestHelpers.MakeDef("OptTestSteelInv");
            var comp = TestHelpers.MakeDef("OptTestCompInv");
            var gold = TestHelpers.MakeDef("OptTestGoldInv");
            fx.Demand[steel] = 70;
            fx.Demand[comp] = 10;
            fx.Demand[gold] = 10;
            fx.Add(steel, new IntVec3(5, 0, 5), 40, 1f);
            fx.Add(steel, new IntVec3(18, 0, 4), 40, 1f);
            fx.Add(comp, new IntVec3(6, 0, 5), 10, 1f);
            fx.Add(gold, new IntVec3(18, 0, 5), 10, 0.6f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            Assert.True(plan.Trips.Count >= 2, "mass forces at least two trips");
        }

        [Fact]
        public void OpCountInstrument_TypicalInstanceStaysSmall()
        {
            var fx = new Fixture();
            fx.CapacityKg = 35f;
            var steel = TestHelpers.MakeDef("OptTestSteelOps");
            var comp = TestHelpers.MakeDef("OptTestCompOps");
            fx.Demand[steel] = 50;
            fx.Demand[comp] = 10;
            fx.Add(steel, new IntVec3(5, 0, 5), 30, 1f);
            fx.Add(steel, new IntVec3(15, 0, 5), 30, 1f);
            fx.Add(comp, new IntVec3(6, 0, 5), 10, 1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            Assert.True(ThoroughHaulPlanner.LastPlanPartitionSteps > 0,
                "partition DP should have run");
            Assert.True(ThoroughHaulPlanner.LastPlanPartitionSteps < 100_000,
                $"typical instance used {ThoroughHaulPlanner.LastPlanPartitionSteps} DP steps");
        }

        // ------------------------------------------------------------------
        // Joint sourcing
        // ------------------------------------------------------------------

        [Fact]
        public void JointSourcing_PrefersCoLocatedStacksOverNearest()
        {
            // defA has a stack near the workbench (A2) and a stack co-located
            // with defB's only stack far east (A1). Greedy nearest-first
            // sourcing (Sweep, Sequential) grabs A2 and pays both a west and
            // an east leg; joint sourcing takes A1 and does one east run.
            var fx = new Fixture();
            var defA = TestHelpers.MakeDef("OptTestJointA");
            var defB = TestHelpers.MakeDef("OptTestJointB");
            fx.Demand[defA] = 10;
            fx.Demand[defB] = 10;
            Thing a1 = fx.Add(defA, new IntVec3(20, 0, 10), 50, 0.1f);
            Thing a2 = fx.Add(defA, new IntVec3(7, 0, 10), 50, 0.1f);
            fx.Add(defB, new IntVec3(20, 0, 11), 50, 0.1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            var things = TestHelpers.AllPickups(plan).Select(p => p.Thing).ToList();
            Assert.Contains(a1, things);
            Assert.DoesNotContain(a2, things);
            Assert.Equal(22, PlanCost(fx, plan)); // wb→(20,10)→(20,11)→wb
        }

        // ------------------------------------------------------------------
        // Virtual copies (split one stack across trips)
        // ------------------------------------------------------------------

        [Fact]
        public void OversizedStack_SplitsAcrossTrips()
        {
            // ctVol = min(750, 75/1.0) = 75; B = 71.25. Whole take of 100 at
            // u=4: invMass = 400 - 75*4 = 100 > B → split into chunks 92
            // (CT 75 + inv 17, invMass 68) and 8. The same Thing legally
            // spans two trips — the case the earlier spec draft punted on.
            var fx = new Fixture();
            var big = TestHelpers.MakeDef("OptTestBigStack", stackLimit: 750, smallVolume: false);
            fx.Demand[big] = 100;
            Thing stack = fx.Add(big, new IntVec3(5, 0, 5), 100, 4f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            Assert.Equal(2, plan.Trips.Count);
            Assert.All(TestHelpers.AllPickups(plan), p => Assert.Same(stack, p.Thing));

            var tripTotals = plan.Trips
                .Select(t => t.Pickups.Sum(p => p.Count))
                .OrderBy(c => c)
                .ToList();
            Assert.Equal(new List<int> { 8, 92 }, tripTotals);

            var bigTrip = plan.Trips.Single(t => t.Pickups.Sum(p => p.Count) == 92);
            var ct = bigTrip.Pickups.Single(p => p.Destination == PickupDestination.CarryTracker);
            var inv = bigTrip.Pickups.Single(p => p.Destination == PickupDestination.Inventory);
            Assert.Equal(75, ct.Count);
            Assert.Equal(17, inv.Count);
        }

        // ------------------------------------------------------------------
        // Storage grouping
        // ------------------------------------------------------------------

        [Fact]
        public void GroupedStacks_AreOneSourcingDecision()
        {
            // The two grouped stacks (avail 10+10) form the only minimal
            // cover of demand 20 — the near singleton (avail 10) can't cover
            // alone and adding it to the group is non-minimal. The plan must
            // draw from both group members and leave the singleton alone.
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestGroupSteel");
            fx.Demand[steel] = 20;
            Thing g1 = fx.Add(steel, new IntVec3(20, 0, 10), 10, 0.1f, groupId: 0);
            Thing g2 = fx.Add(steel, new IntVec3(20, 0, 11), 10, 0.1f, groupId: 0);
            Thing lone = fx.Add(steel, new IntVec3(5, 0, 10), 10, 0.1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            var things = TestHelpers.AllPickups(plan).Select(p => p.Thing).ToList();
            Assert.Contains(g1, things);
            Assert.Contains(g2, things);
            Assert.DoesNotContain(lone, things);
        }

        [Fact]
        public void SpanGuard_SplitsSprawlingGroup()
        {
            // One GroupId spanning 26 cells: un-split it would be the only
            // minimal cover (avail 20 == demand) and force a walk to x=40.
            // The span guard bisects it, so the near half pairs with the
            // singleton instead — cost 16 vs 60 — and the far member is
            // untouched.
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestSpanSteel");
            fx.Demand[steel] = 20;
            Thing near = fx.Add(steel, new IntVec3(14, 0, 10), 10, 0.1f, groupId: 0);
            Thing far = fx.Add(steel, new IntVec3(40, 0, 10), 10, 0.1f, groupId: 0);
            Thing lone = fx.Add(steel, new IntVec3(6, 0, 10), 10, 0.1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            var things = TestHelpers.AllPickups(plan).Select(p => p.Thing).ToList();
            Assert.Contains(near, things);
            Assert.Contains(lone, things);
            Assert.DoesNotContain(far, things);
            Assert.Equal(16, PlanCost(fx, plan));
        }

        [Fact]
        public void LooseStacks_StaySingletonGroups_NodeGuardTrips()
        {
            // 16 GroupId -1 stacks must stay 16 distinct nodes, tripping the
            // 15-node guard (null). A buggy merge into one group would
            // produce a plan instead.
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestLooseSteel");
            fx.Wb = new IntVec3(0, 0, 0);
            fx.Demand[steel] = 16;
            for (int i = 0; i < 16; i++)
                fx.Add(steel, new IntVec3(1 + i, 0, 1), 1, 0.1f);

            Assert.Null(Planner.Plan(fx.Request()));
        }

        [Fact]
        public void FifteenNodes_WorstCaseInsideGuards_Solves()
        {
            // Exactly at the node guard: 15 singleton stacks, all needed
            // (single minimal cover), one combo at the full 3^15 partition
            // DP. Light masses → cheapest plan is one trip visiting all.
            var fx = new Fixture();
            var steel = TestHelpers.MakeDef("OptTestFifteen");
            fx.Wb = new IntVec3(0, 0, 0);
            fx.Demand[steel] = 15;
            for (int i = 0; i < 15; i++)
                fx.Add(steel, new IntVec3(1 + i, 0, 1), 1, 0.1f);

            var plan = Planner.Plan(fx.Request());

            AssertInvariants(fx, plan);
            Assert.Single(plan.Trips);
            Assert.Equal(15, plan.Trips[0].Pickups.Count);
        }

        // ------------------------------------------------------------------
        // Guards
        // ------------------------------------------------------------------

        [Fact]
        public void VirtualCopyGuard_MoreThanFourChunks_ReturnsNull()
        {
            // Chunk capacity at u=4 is 92 (ctVol 75 + floor(71.25/4)); a take
            // of 400 needs five chunks → guard → null.
            var fx = new Fixture();
            var big = TestHelpers.MakeDef("OptTestCopyGuard", stackLimit: 750, smallVolume: false);
            fx.Demand[big] = 400;
            fx.Add(big, new IntVec3(5, 0, 5), 400, 4f);

            Assert.Null(Planner.Plan(fx.Request()));
        }

        [Fact]
        public void UnfittableUnit_ReturnsNull()
        {
            // stackLimit 0 → ctVol 0 (no bypass), and one unit outweighs the
            // budget: no trip can ever carry it. Same silent null as Sweep;
            // the Sequential rung (no mass cap) still hauls it.
            var fx = new Fixture();
            var anvil = TestHelpers.MakeDef("OptTestAnvil", stackLimit: 0, smallVolume: false);
            fx.Demand[anvil] = 1;
            fx.Add(anvil, new IntVec3(5, 0, 5), 1, 80f);

            Assert.Null(Planner.Plan(fx.Request()));
        }

        [Fact]
        public void WorkEstimateGuard_ReturnsNull_BeforeRunningDp()
        {
            // 3 defs x 5 singleton stacks (avail 5, demand 20): every minimal
            // cover is 4-of-5, so 5 covers of 4 nodes per def. Estimated work
            // (5 * 3^4)^3 ≈ 66M > 30M → guard → null without any DP steps.
            var fx = new Fixture();
            for (int d = 0; d < 3; d++)
            {
                var def = TestHelpers.MakeDef("OptTestWork" + d);
                fx.Demand[def] = 20;
                for (int i = 0; i < 5; i++)
                    fx.Add(def, new IntVec3(2 + i * 3, 0, 2 + d * 6), 5, 0.1f);
            }

            Assert.Null(Planner.Plan(fx.Request()));
            Assert.Equal(0, ThoroughHaulPlanner.LastPlanPartitionSteps);
        }

        // ------------------------------------------------------------------
        // Determinism
        // ------------------------------------------------------------------

        [Fact]
        public void IdenticalRequests_YieldIdenticalPlans()
        {
            string Run()
            {
                var fx = new Fixture();
                fx.CapacityKg = 40f;
                var defA = TestHelpers.MakeDef("OptTestDetA");
                var defB = TestHelpers.MakeDef("OptTestDetB");
                fx.Demand[defA] = 30;
                fx.Demand[defB] = 12;
                fx.Add(defA, new IntVec3(20, 0, 10), 20, 1f);
                fx.Add(defA, new IntVec3(7, 0, 10), 20, 1f);
                fx.Add(defA, new IntVec3(12, 0, 18), 20, 1f);
                fx.Add(defB, new IntVec3(20, 0, 11), 12, 1f);
                var plan = Planner.Plan(fx.Request());
                AssertInvariants(fx, plan);
                return Describe(fx, plan);
            }

            Assert.Equal(Run(), Run());
        }

        // ------------------------------------------------------------------
        // Brute-force oracle
        // ------------------------------------------------------------------

        private sealed class OracleStack
        {
            public IntVec3 Pos;
            public int Avail;
            public float UnitMass;
            public int CtVol;
            public int DefIndex;
        }

        /// <summary>
        /// Exhaustive optimum over ALL plans, including ones that split a
        /// stack across trips: DP over "units taken so far per stack", where
        /// each transition is one feasible trip (any take vector under the
        /// shared capacity model) costed by brute-force tour enumeration.
        /// Returns null when no sequence of feasible trips meets demand.
        /// </summary>
        private static int? OracleSolve(
            List<OracleStack> stacks, int[] demand, float budget, IntVec3 wb)
        {
            int n = stacks.Count;
            var weight = new int[n];
            int states = 1;
            for (int i = 0; i < n; i++)
            {
                weight[i] = states;
                states *= stacks[i].Avail + 1;
            }

            var tourByMask = new int[1 << n];
            for (int mask = 1; mask < 1 << n; mask++)
            {
                var unique = new List<IntVec3>();
                for (int i = 0; i < n; i++)
                {
                    if ((mask & (1 << i)) != 0 && !unique.Contains(stacks[i].Pos))
                        unique.Add(stacks[i].Pos);
                }
                tourByMask[mask] = BruteTour(unique, wb);
            }

            const int INF = int.MaxValue;
            var f = new int[states];
            for (int s = 0; s < states; s++) f[s] = INF;
            f[0] = 0;

            var taken = new int[n];
            int curState = 0;

            void EnumerateTrips(int idx, int mask, float mass, float bestByp, int offset)
            {
                if (idx == n)
                {
                    if (mask == 0) return;
                    if (mass - bestByp > budget + 1e-3f) return;
                    int target = curState + offset;
                    int cand = f[curState] + tourByMask[mask];
                    if (cand < f[target]) f[target] = cand;
                    return;
                }
                int max = stacks[idx].Avail - taken[idx];
                for (int t = 0; t <= max; t++)
                {
                    EnumerateTrips(
                        idx + 1,
                        t > 0 ? mask | (1 << idx) : mask,
                        mass + t * stacks[idx].UnitMass,
                        t > 0
                            ? Math.Max(bestByp,
                                Math.Min(t, stacks[idx].CtVol) * stacks[idx].UnitMass)
                            : bestByp,
                        offset + t * weight[idx]);
                }
            }

            for (int s = 0; s < states; s++)
            {
                if (f[s] == INF) continue;
                int rem = s;
                for (int i = 0; i < n; i++)
                {
                    taken[i] = rem % (stacks[i].Avail + 1);
                    rem /= stacks[i].Avail + 1;
                }
                curState = s;
                EnumerateTrips(0, 0, 0f, 0f, 0);
            }

            int best = INF;
            var defTotals = new int[demand.Length];
            for (int s = 0; s < states; s++)
            {
                if (f[s] == INF) continue;
                Array.Clear(defTotals, 0, defTotals.Length);
                int rem = s;
                for (int i = 0; i < n; i++)
                {
                    defTotals[stacks[i].DefIndex] += rem % (stacks[i].Avail + 1);
                    rem /= stacks[i].Avail + 1;
                }
                bool match = true;
                for (int d = 0; d < demand.Length; d++)
                {
                    if (defTotals[d] != demand[d]) { match = false; break; }
                }
                if (match && f[s] < best) best = f[s];
            }
            return best == INF ? (int?)null : best;
        }

        [Fact]
        public void Oracle_JointSourcingFixture_MatchesOptimum()
        {
            var fx = new Fixture();
            var defA = TestHelpers.MakeDef("OptTestOracleA");
            var defB = TestHelpers.MakeDef("OptTestOracleB");
            fx.Demand[defA] = 4;
            fx.Demand[defB] = 4;
            fx.Add(defA, new IntVec3(20, 0, 10), 4, 0.5f);
            fx.Add(defA, new IntVec3(7, 0, 10), 4, 0.5f);
            fx.Add(defB, new IntVec3(20, 0, 11), 4, 0.5f);

            var stacks = new List<OracleStack>
            {
                new OracleStack { Pos = new IntVec3(20, 0, 10), Avail = 4, UnitMass = 0.5f,
                    CtVol = HaulMath.MaxStackSpaceEver(defA, fx.CapacityKg), DefIndex = 0 },
                new OracleStack { Pos = new IntVec3(7, 0, 10), Avail = 4, UnitMass = 0.5f,
                    CtVol = HaulMath.MaxStackSpaceEver(defA, fx.CapacityKg), DefIndex = 0 },
                new OracleStack { Pos = new IntVec3(20, 0, 11), Avail = 4, UnitMass = 0.5f,
                    CtVol = HaulMath.MaxStackSpaceEver(defB, fx.CapacityKg), DefIndex = 1 },
            };

            var plan = Planner.Plan(fx.Request());
            int? oracle = OracleSolve(stacks, new[] { 4, 4 }, fx.InventoryBudget, fx.Wb);

            AssertInvariants(fx, plan);
            Assert.Equal(oracle, PlanCost(fx, plan));
            Assert.Equal(22, oracle);
        }

        [Fact]
        public void Oracle_SplitStackFixture_MatchesOptimum()
        {
            // ctVol = min(stackLimit 3, capacity 10) = 3; B = 9.5. A take of
            // 8 at u=2 can't ride one trip (16 - 6 = 10 > 9.5) — the optimum
            // must split the stack across trips, which hand-computed cases
            // can't easily cover.
            var fx = new Fixture();
            fx.CapacityKg = 10f;
            var ore = TestHelpers.MakeDef("OptTestOracleOre", stackLimit: 3, smallVolume: false);
            fx.Demand[ore] = 8;
            fx.Add(ore, new IntVec3(14, 0, 10), 8, 2f);

            var stacks = new List<OracleStack>
            {
                new OracleStack { Pos = new IntVec3(14, 0, 10), Avail = 8, UnitMass = 2f,
                    CtVol = HaulMath.MaxStackSpaceEver(ore, fx.CapacityKg), DefIndex = 0 },
            };

            var plan = Planner.Plan(fx.Request());
            int? oracle = OracleSolve(stacks, new[] { 8 }, fx.InventoryBudget, fx.Wb);

            AssertInvariants(fx, plan);
            Assert.NotNull(oracle);
            Assert.Equal(oracle, PlanCost(fx, plan));
        }

        [Fact]
        public void Oracle_RandomizedSmallInstances_MatchOptimum()
        {
            // Seeded, deterministic sweep of tiny instances (n <= 4 stacks,
            // singleton groups) against the exhaustive oracle, in two regimes
            // chosen to stay inside the planner's exactness envelope (the
            // spec's accepted canonical-count approximation binds only when
            // tight capacity meets multi-stack defs):
            //   - loose rounds: capacity above total mass, multiple stacks
            //     per def — exercises sourcing + routing exactness.
            //   - tight rounds: binding capacity, one stack per def —
            //     exercises chunking (split-stack trips) + partitioning.
            var rng = new Random(20260611);
            float[] massOptions = { 0.5f, 1f, 2f };

            for (int round = 0; round < 60; round++)
            {
                bool loose = round < 30;
                int defCount = 1 + rng.Next(loose ? 2 : 3);
                int stackCount = loose ? defCount + rng.Next(5 - defCount) : defCount;

                var defs = new ThingDef[defCount];
                var defMass = new float[defCount];
                for (int d = 0; d < defCount; d++)
                {
                    defs[d] = TestHelpers.MakeDef(
                        $"OptTestRng{round}_{d}",
                        stackLimit: 3 + rng.Next(5),
                        smallVolume: false);
                    defMass[d] = massOptions[rng.Next(massOptions.Length)];
                }

                var specs = new List<(int Def, IntVec3 Pos, int Avail)>();
                float totalMass = 0f;
                var defAvail = new int[defCount];
                for (int i = 0; i < stackCount; i++)
                {
                    int d = i < defCount ? i : rng.Next(defCount);
                    var pos = new IntVec3(rng.Next(13), 0, rng.Next(13));
                    int avail = 1 + rng.Next(4);
                    specs.Add((d, pos, avail));
                    totalMass += avail * defMass[d];
                    defAvail[d] += avail;
                }

                float capacity = loose
                    ? totalMass / 0.95f + 5f
                    : 6 + rng.Next(10);

                var fx = new Fixture();
                fx.CapacityKg = capacity;
                fx.Wb = new IntVec3(6, 0, 6);
                var stacks = new List<OracleStack>();
                foreach ((int Def, IntVec3 Pos, int Avail) spec in specs)
                {
                    fx.Add(defs[spec.Def], spec.Pos, spec.Avail, defMass[spec.Def]);
                    stacks.Add(new OracleStack
                    {
                        Pos = spec.Pos,
                        Avail = spec.Avail,
                        UnitMass = defMass[spec.Def],
                        CtVol = HaulMath.MaxStackSpaceEver(defs[spec.Def], capacity),
                        DefIndex = spec.Def,
                    });
                }

                var demand = new int[defCount];
                for (int d = 0; d < defCount; d++)
                {
                    demand[d] = 1 + rng.Next(defAvail[d]);
                    fx.Demand[defs[d]] = demand[d];
                }

                var plan = Planner.Plan(fx.Request());
                int? oracle = OracleSolve(stacks, demand, fx.InventoryBudget, fx.Wb);

                if (oracle == null)
                {
                    Assert.True(plan == null, $"round {round}: oracle infeasible but planner produced a plan");
                    continue;
                }
                Assert.True(plan != null, $"round {round}: oracle found cost {oracle} but planner returned null");
                AssertInvariants(fx, plan);
                int got = PlanCost(fx, plan);
                Assert.True(oracle == got,
                    $"round {round}: oracle optimum {oracle} but planner cost {got}");
            }
        }

        // ------------------------------------------------------------------
        // Comparative property vs Sweep
        // ------------------------------------------------------------------

        [Fact]
        public void Thorough_NeverCostsMoreThanSweep()
        {
            // Singleton groups → zero representative-position error, so the
            // comparison needs no tolerance. Fresh fixtures per planner:
            // planners may re-sort pool lists in place.
            var sweep = new SweepHaulPlanner();

            for (int scenario = 0; scenario < 3; scenario++)
            {
                Fixture Build()
                {
                    var fx = new Fixture();
                    switch (scenario)
                    {
                        case 0: // joint-sourcing trap
                            var defA = TestHelpers.MakeDef("OptTestCmpA");
                            var defB = TestHelpers.MakeDef("OptTestCmpB");
                            fx.Demand[defA] = 10;
                            fx.Demand[defB] = 10;
                            fx.Add(defA, new IntVec3(20, 0, 10), 50, 0.1f);
                            fx.Add(defA, new IntVec3(7, 0, 10), 50, 0.1f);
                            fx.Add(defB, new IntVec3(20, 0, 11), 50, 0.1f);
                            break;
                        case 1: // east/west clusters under tight mass
                            fx.CapacityKg = 50f;
                            var steelE = TestHelpers.MakeDef("OptTestCmpSteelE");
                            var compE = TestHelpers.MakeDef("OptTestCmpCompE");
                            var goldW = TestHelpers.MakeDef("OptTestCmpGoldW");
                            fx.Demand[steelE] = 50;
                            fx.Demand[compE] = 5;
                            fx.Demand[goldW] = 47;
                            fx.Add(steelE, new IntVec3(20, 0, 10), 50, 1f);
                            fx.Add(compE, new IntVec3(20, 0, 11), 5, 1f);
                            fx.Add(goldW, new IntVec3(5, 0, 10), 47, 1f);
                            break;
                        default: // oversized stack split
                            var big = TestHelpers.MakeDef("OptTestCmpBig", stackLimit: 750, smallVolume: false);
                            fx.Demand[big] = 100;
                            fx.Add(big, new IntVec3(5, 0, 5), 100, 4f);
                            break;
                    }
                    return fx;
                }

                Fixture fxThorough = Build();
                Fixture fxSweep = Build();
                var planThorough = Planner.Plan(fxThorough.Request());
                var planSweep = sweep.Plan(fxSweep.Request());

                Assert.NotNull(planThorough);
                Assert.NotNull(planSweep);
                AssertInvariants(fxThorough, planThorough);
                int costThorough = PlanCost(fxThorough, planThorough);
                int costSweep = PlanCost(fxSweep, planSweep);
                Assert.True(costThorough <= costSweep,
                    $"scenario {scenario}: Thorough cost {costThorough} > Sweep cost {costSweep}");
            }
        }
    }
}

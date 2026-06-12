using System.Collections.Generic;
using System.Linq;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;
using Xunit;

namespace UniqueWeaponsUnbound.Tests
{
    /// <summary>
    /// Property tests for SweepHaulPlanner's hybrid bin-pack. These guard
    /// the per-trip invariants the JobDriver's hybrid haul chain relies on:
    /// at most one CarryTracker pickup per trip, inventory mass under the
    /// configured budget, and total counts equal to demand.
    /// </summary>
    public class SweepHaulPlannerTests
    {
        private static readonly SweepHaulPlanner Planner = new SweepHaulPlanner();
        private const float CapacityFactor = 0.95f;

        [Fact]
        public void EmptyDemand_ReturnsEmptyHybridPlan()
        {
            var request = TestHelpers.MakeRequest();
            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            Assert.True(plan.IsValid);
            Assert.Empty(plan.Trips);
            Assert.Equal(HaulPlanExecutionStrategy.UwuCarryInventoryHybrid, plan.ExecutionStrategy);
        }

        [Fact]
        public void HybridStrategyOnEverySuccessfulPlan()
        {
            var steel = TestHelpers.MakeDef("TestSteel");
            var request = TestHelpers.MakeRequest(
                demand: new Dictionary<ThingDef, int> { [steel] = 30 },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 50, 1f),
                    },
                });

            var plan = Planner.Plan(request);
            Assert.NotNull(plan);
            Assert.Equal(HaulPlanExecutionStrategy.UwuCarryInventoryHybrid, plan.ExecutionStrategy);
        }

        [Fact]
        public void UserScenario_ThreeStacksOneTrip()
        {
            // 35 kg pawn, 40 kg steel + 10 kg components + 10 kg gold, all
            // clustered. Expected: one trip — heaviest (steel) goes to CT
            // (volume permits since stackLimit=75), components and gold
            // ride inventory under the 33.25 kg budget.
            var steel = TestHelpers.MakeDef("TestSteel");
            var components = TestHelpers.MakeDef("TestComponents");
            var gold = TestHelpers.MakeDef("TestGold");
            var request = TestHelpers.MakeRequest(
                capacityKg: 35f,
                currentEncumbranceKg: 0f,
                demand: new Dictionary<ThingDef, int>
                {
                    [steel] = 40,
                    [components] = 10,
                    [gold] = 10,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 40, 1f),
                    },
                    [components] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(components, new IntVec3(6, 0, 5), 10, 1f),
                    },
                    [gold] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(gold, new IntVec3(7, 0, 5), 10, 1f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            Assert.Single(plan.Trips);
            var trip = plan.Trips[0];
            Assert.Equal(3, trip.Pickups.Count);

            // Steel becomes the trip's CarryTracker pickup.
            var steelPickup = trip.Pickups.Single(p => p.Thing.def == steel);
            Assert.Equal(PickupDestination.CarryTracker, steelPickup.Destination);
            Assert.Equal(40, steelPickup.Count);

            // Components and gold ride inventory.
            var compPickup = trip.Pickups.Single(p => p.Thing.def == components);
            var goldPickup = trip.Pickups.Single(p => p.Thing.def == gold);
            Assert.Equal(PickupDestination.Inventory, compPickup.Destination);
            Assert.Equal(PickupDestination.Inventory, goldPickup.Destination);
        }

        [Fact]
        public void AtMostOneCarryTrackerPickupPerTrip()
        {
            var steel = TestHelpers.MakeDef("TestSteel");
            var plasteel = TestHelpers.MakeDef("TestPlasteel");
            var components = TestHelpers.MakeDef("TestComponents");
            var request = TestHelpers.MakeRequest(
                capacityKg: 75f,
                demand: new Dictionary<ThingDef, int>
                {
                    [steel] = 30,
                    [plasteel] = 30,
                    [components] = 20,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 30, 1f),
                    },
                    [plasteel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(plasteel, new IntVec3(6, 0, 5), 30, 3f),
                    },
                    [components] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(components, new IntVec3(7, 0, 5), 20, 1f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            foreach (var trip in plan.Trips)
            {
                int ctCount = trip.Pickups.Count(p => p.Destination == PickupDestination.CarryTracker);
                Assert.True(ctCount <= 1, $"Trip has {ctCount} CarryTracker pickups; expected at most 1");
            }
        }

        [Fact]
        public void InventoryMassPerTripUnderBudget()
        {
            var steel = TestHelpers.MakeDef("TestSteel");
            var plasteel = TestHelpers.MakeDef("TestPlasteel");
            float capacityKg = 75f;
            float invBudget = capacityKg * CapacityFactor;
            var request = TestHelpers.MakeRequest(
                capacityKg: capacityKg,
                demand: new Dictionary<ThingDef, int>
                {
                    [steel] = 100,
                    [plasteel] = 50,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 100, 1f),
                    },
                    [plasteel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(plasteel, new IntVec3(6, 0, 5), 50, 3f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            // HaulPickup doesn't carry MassPerUnit through to the output, so
            // we re-derive mass from the test inputs by def.
            foreach (var trip in plan.Trips)
            {
                float invMass = 0f;
                foreach (var p in trip.Pickups.Where(p => p.Destination == PickupDestination.Inventory))
                {
                    float mpu = p.Thing.def == steel ? 1f : (p.Thing.def == plasteel ? 3f : 0f);
                    invMass += p.Count * mpu;
                }
                Assert.True(invMass <= invBudget + 0.001f,
                    $"Trip inventory mass {invMass} exceeds budget {invBudget}");
            }
        }

        [Fact]
        public void TotalsExactlyMatchDemand()
        {
            var steel = TestHelpers.MakeDef("TestSteel");
            var components = TestHelpers.MakeDef("TestComponents");
            var request = TestHelpers.MakeRequest(
                capacityKg: 50f,
                demand: new Dictionary<ThingDef, int>
                {
                    [steel] = 73,
                    [components] = 17,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 50, 1f),
                        TestHelpers.MakeCandidate(steel, new IntVec3(8, 0, 5), 50, 1f),
                    },
                    [components] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(components, new IntVec3(12, 0, 5), 30, 0.6f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            var totals = TestHelpers.TotalsByDef(plan);
            Assert.Equal(73, totals[steel]);
            Assert.Equal(17, totals[components]);
        }

        [Fact]
        public void LargeStackOverflows_SplitsAcrossCtAndInventory()
        {
            // Mod-bumped stack limit (e.g. Stack Gap with 750 plasteel) but
            // small VolumePerUnit. Demand exceeds carry-tracker volume budget
            // (capacity / volumePerUnit) → planner must split: some count
            // into CT, the rest as Inventory pickups in the same trip when
            // the inventory budget can absorb them.
            //
            // smallVolume=false → VolumePerUnit=1.0, capacity=75 →
            // MaxStackSpaceEver = min(stackLimit=750, 75) = 75 units in CT.
            var bigDef = TestHelpers.MakeDef("TestBigStack", stackLimit: 750, smallVolume: false);
            var request = TestHelpers.MakeRequest(
                capacityKg: 75f,
                demand: new Dictionary<ThingDef, int> { [bigDef] = 100 },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [bigDef] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(bigDef, new IntVec3(5, 0, 5), 100, 0.1f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            var totals = TestHelpers.TotalsByDef(plan);
            Assert.Equal(100, totals[bigDef]);

            // Single trip — CT can take 75, the remaining 25 as Inventory
            // (mass = 25 * 0.1 = 2.5 kg, well under budget).
            Assert.Single(plan.Trips);
            var ctPickup = plan.Trips[0].Pickups.Single(p => p.Destination == PickupDestination.CarryTracker);
            Assert.Equal(75, ctPickup.Count);
            var invPickup = plan.Trips[0].Pickups.Single(p => p.Destination == PickupDestination.Inventory);
            Assert.Equal(25, invPickup.Count);
        }

        [Fact]
        public void DemandExceedsPool_ReturnsNull()
        {
            var steel = TestHelpers.MakeDef("TestSteel");
            var request = TestHelpers.MakeRequest(
                demand: new Dictionary<ThingDef, int> { [steel] = 200 },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steel] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steel, new IntVec3(5, 0, 5), 50, 1f),
                    },
                });

            var plan = Planner.Plan(request);
            Assert.Null(plan);
        }

        [Fact]
        public void RepairMove_ResolvesMixingViaCtOnlyTrip()
        {
            // Configuration that defeats the swap-only Repair primitive: the
            // raw bin-pack puts the heaviest stack (east) into trip 1 CT and
            // overflows a west inv pickup into trip 1 alongside it; trip 2 is
            // left CT-only with the small east leftover. Swap can't help —
            // trip 2 has no Inventory pickup to swap against. Move relocates
            // the misplaced west inv pickup into trip 2, where the workbench
            // → west leg is already paid by the CT pickup at west, so the
            // total Held-Karp cost strictly drops.
            var steelE = TestHelpers.MakeDef("TestSteelE");
            var compE = TestHelpers.MakeDef("TestCompE");
            var goldW = TestHelpers.MakeDef("TestGoldW");
            var workbench = new IntVec3(10, 0, 10);
            var request = TestHelpers.MakeRequest(
                capacityKg: 50f,
                workbenchPos: workbench,
                demand: new Dictionary<ThingDef, int>
                {
                    [steelE] = 50,
                    [compE] = 5,
                    [goldW] = 47,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [steelE] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(steelE, new IntVec3(20, 0, 10), 50, 1f),
                    },
                    [compE] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(compE, new IntVec3(20, 0, 11), 5, 1f),
                    },
                    [goldW] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(goldW, new IntVec3(5, 0, 10), 47, 1f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            Assert.Equal(2, plan.Trips.Count);

            // Each trip should be cluster-pure after Repair: no trip mixes
            // east and west pickups.
            foreach (var trip in plan.Trips)
            {
                bool hasEast = trip.Pickups.Any(p =>
                    p.Thing.def == steelE || p.Thing.def == compE);
                bool hasWest = trip.Pickups.Any(p => p.Thing.def == goldW);
                Assert.False(hasEast && hasWest,
                    $"Trip mixes east and west pickups: {string.Join(",", trip.Pickups.Select(p => p.Thing.def.defName))}");
            }
        }

        [Fact]
        public void AngleRotation_StraddlingCluster_PlanIsValid()
        {
            // Two western pickups straddle the ±π discontinuity (one slightly
            // north of due-west, one slightly south). Plain Atan2-ascending
            // sort places them at the start and end of the iteration,
            // separated by the eastern filler — a layout that historically
            // produced cluster-split bin-packs. SortByRotatedAngle shifts the
            // sweep origin past the largest empty arc (the eastern void), so
            // both western items sit contiguously and the bin-pack treats
            // them as one cluster.
            //
            // Smoke test: existing planner invariants (demand met, at most
            // one CT pickup per trip) hold under the rotation. Detailed cost
            // assertions belong in ThoroughHaulPlannerTests once that planner
            // exists.
            var west = TestHelpers.MakeDef("TestWestDef");
            var east = TestHelpers.MakeDef("TestEastDef");
            var workbench = new IntVec3(10, 0, 10);
            var request = TestHelpers.MakeRequest(
                capacityKg: 50f,
                workbenchPos: workbench,
                demand: new Dictionary<ThingDef, int>
                {
                    [west] = 20,
                    [east] = 30,
                },
                pool: new Dictionary<ThingDef, List<HaulCandidate>>
                {
                    [west] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(west, new IntVec3(0, 0, 11), 10, 1f),
                        TestHelpers.MakeCandidate(west, new IntVec3(0, 0, 9), 10, 1f),
                    },
                    [east] = new List<HaulCandidate>
                    {
                        TestHelpers.MakeCandidate(east, new IntVec3(20, 0, 10), 30, 1f),
                    },
                });

            var plan = Planner.Plan(request);

            Assert.NotNull(plan);
            var totals = TestHelpers.TotalsByDef(plan);
            Assert.Equal(20, totals[west]);
            Assert.Equal(30, totals[east]);
            foreach (var trip in plan.Trips)
            {
                int ctCount = trip.Pickups.Count(p => p.Destination == PickupDestination.CarryTracker);
                Assert.True(ctCount <= 1, $"Trip has {ctCount} CarryTracker pickups; expected at most 1");
            }
        }
    }
}

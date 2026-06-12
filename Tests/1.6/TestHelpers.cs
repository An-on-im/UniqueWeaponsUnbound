using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UniqueWeaponsUnbound.HaulPlanning;
using Verse;

namespace UniqueWeaponsUnbound.Tests
{
    /// <summary>
    /// Synthetic data builders for planner tests. The planners are pure (no
    /// Find.* / no live world state), so these only need to populate the
    /// fields the planner actually reads: ThingDef.stackLimit and
    /// ThingDef.smallVolume (which drives VolumePerUnit), plus the
    /// HaulCandidate quartet (Thing, Position, AvailableCount, MassPerUnit).
    /// </summary>
    internal static class TestHelpers
    {
        // ThingDef.smallVolume drives the VolumePerUnit getter that
        // SweepHaulPlanner consults (true → 0.1 per unit, false → 1.0 per
        // unit). Resolved by reflection so we don't bind to a public field
        // we'd otherwise touch directly — keeps this helper resilient if the
        // visibility ever changes upstream.
        private static readonly FieldInfo SmallVolumeField =
            typeof(ThingDef).GetField("smallVolume", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Calling `new ThingDef()` runs BuildableDef's instance ctor, which
        // triggers Verse.BaseContent..cctor → UnityEngine.Resources.Load and
        // fails with "ECall methods must be packaged into a system module"
        // outside a Unity runtime. GetUninitializedObject allocates without
        // running any constructors, sidestepping the Unity init chain.
        public static ThingDef MakeDef(string defName, int stackLimit = 75, bool smallVolume = true)
        {
            var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            def.defName = defName;
            def.stackLimit = stackLimit;
            SmallVolumeField?.SetValue(def, smallVolume);
            return def;
        }

        // Thing.Equals/GetHashCode key on thingIDNumber (+ def), and an
        // uninitialized Thing has id 0 — so without unique ids, every test
        // Thing of one def is "equal" and Dictionary<Thing, ...> lookups (in
        // planners and test fixtures alike) silently merge distinct stacks.
        // In game ThingMaker assigns unique ids; mirror that here.
        private static int nextThingId = 1;

        public static Thing MakeThing(ThingDef def, int stackCount)
        {
            var thing = (Thing)FormatterServices.GetUninitializedObject(typeof(Thing));
            thing.def = def;
            thing.stackCount = stackCount;
            thing.thingIDNumber = nextThingId++;
            return thing;
        }

        // groupId defaults to -1 ("not in storage"), which group-aware
        // planners treat as a singleton group — the right default for tests
        // that reason at stack granularity. Pass a shared non-negative id to
        // model stacks in one stockpile/shelf.
        public static HaulCandidate MakeCandidate(
            ThingDef def, IntVec3 position, int count, float massPerUnit,
            int groupId = -1)
        {
            return new HaulCandidate
            {
                Thing = MakeThing(def, count),
                Position = position,
                AvailableCount = count,
                MassPerUnit = massPerUnit,
                GroupId = groupId,
            };
        }

        public static HaulPlanRequest MakeRequest(
            float capacityKg = 75f,
            float currentEncumbranceKg = 0f,
            IntVec3? pawnPos = null,
            IntVec3? workbenchPos = null,
            Dictionary<ThingDef, int> demand = null,
            Dictionary<ThingDef, List<HaulCandidate>> pool = null)
        {
            return new HaulPlanRequest
            {
                PawnPosition = pawnPos ?? new IntVec3(0, 0, 0),
                WorkbenchPosition = workbenchPos ?? new IntVec3(10, 0, 10),
                CapacityKg = capacityKg,
                CurrentEncumbranceKg = currentEncumbranceKg,
                Demand = demand ?? new Dictionary<ThingDef, int>(),
                Pool = pool ?? new Dictionary<ThingDef, List<HaulCandidate>>(),
            };
        }

        /// <summary>
        /// Sums up Count by Thing.def across every pickup in every trip.
        /// </summary>
        public static Dictionary<ThingDef, int> TotalsByDef(HaulPlan plan)
        {
            var totals = new Dictionary<ThingDef, int>();
            foreach (HaulTrip trip in plan.Trips)
            {
                foreach (HaulPickup pickup in trip.Pickups)
                {
                    totals.TryGetValue(pickup.Thing.def, out int t);
                    totals[pickup.Thing.def] = t + pickup.Count;
                }
            }
            return totals;
        }

        public static IEnumerable<HaulPickup> AllPickups(HaulPlan plan)
        {
            return plan.Trips.SelectMany(t => t.Pickups);
        }
    }
}

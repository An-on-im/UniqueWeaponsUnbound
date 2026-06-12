using System.Collections.Generic;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /// <summary>
    /// Inputs to a HaulPlanner. Construct once per planning attempt; immutable
    /// from the planner's perspective.
    /// </summary>
    public class HaulPlanRequest
    {
        /// <summary>Pawn's current map position.</summary>
        public IntVec3 PawnPosition;

        /// <summary>Workbench position — every trip terminates here.</summary>
        public IntVec3 WorkbenchPosition;

        /// <summary>
        /// Pawn's total carry capacity in kg (MassUtility.Capacity). Used as the
        /// per-trip mass budget after subtracting CurrentEncumbranceKg.
        /// </summary>
        public float CapacityKg;

        /// <summary>
        /// Mass already on the pawn (gear + inventory) before the haul phase
        /// starts, in kg. Each trip's pickups must keep total mass under
        /// CapacityKg.
        /// </summary>
        public float CurrentEncumbranceKg;

        /// <summary>
        /// What the spec needs, by def. The planner must satisfy each entry's
        /// count exactly; over- or under-fulfilling is a planner bug.
        /// </summary>
        public Dictionary<ThingDef, int> Demand;

        /// <summary>
        /// Available stacks on the map, grouped by def. Each candidate has
        /// already passed reach/forbidden/CanReserve gating; the planner is
        /// free to choose any subset that meets demand.
        /// </summary>
        public Dictionary<ThingDef, List<HaulCandidate>> Pool;
    }

    /// <summary>
    /// A single on-map stack the planner may draw from. Position and mass are
    /// snapshotted at request-build time so the planner doesn't touch live
    /// world state.
    /// </summary>
    public struct HaulCandidate
    {
        public Thing Thing;
        public IntVec3 Position;
        public int AvailableCount;
        public float MassPerUnit;

        /// <summary>
        /// Identity of the storage SlotGroup (stockpile zone, shelf, storage
        /// building) this stack sits in, snapshotted at pool-build time.
        /// Stacks in the same group share an id (>= 0); stacks outside storage
        /// get -1 and are treated by group-aware planners as singleton groups.
        /// Ids index the request's own group table — they are not stable
        /// across requests. Only populated when the active planner sets
        /// <see cref="IHaulPlanner.GroupPoolBySlotGroup"/>; planners without
        /// grouping never read it. Deliberately keyed on SlotGroup rather than
        /// StorageGroup (linked storage settings) — linked buildings can span
        /// the map and share no locality; SlotGroup is the locality unit.
        /// </summary>
        public int GroupId;
    }
}

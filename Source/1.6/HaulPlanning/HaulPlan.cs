using System.Collections.Generic;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    // A planner's output: an ordered list of trips plus the execution strategy
    // the JobDriver should use to run them. Each trip is a single round-trip
    // from the workbench, picking up one or more stacks before returning.
    // Reservation and queue encoding happen downstream.
    public class HaulPlan
    {
        public List<HaulTrip> Trips;

        // How the JobDriver should execute this plan's trips. Planners that
        // emit one CarryTracker pickup per trip with no inventory routing
        // should set HaulPlanExecutionStrategy.VanillaCarryOnly to opt into the
        // simple, vanilla-toil-only haul chain. Planners that pack multiple
        // pickups per trip with destination metadata (CarryTracker for one
        // stack + Inventory for the rest) must set
        // HaulPlanExecutionStrategy.UwuCarryInventoryHybrid.
        public HaulPlanExecutionStrategy ExecutionStrategy;

        public bool IsValid => Trips != null;
    }

    // Selects which haul-phase toil chain the JobDriver builds for a plan.
    // Default VanillaCarryOnly matches the pre-Sweep behavior exactly so plans
    // missing this field (e.g. an unset literal) and old saves resume on the
    // bedrock path.
    public enum HaulPlanExecutionStrategy
    {
        // Pawn picks up one stack per trip via the carry tracker, walks back to
        // the workbench, drops, repeats. Vanilla Toils_Haul.StartCarryThing +
        // the original dropIngredient toil + JumpIfHaveTargetInQueue.
        // Volume-cap splits handled by vanilla's putRemainderInQueue=true.
        VanillaCarryOnly = 0,

        // Per-trip: at most one CarryTracker pickup (volume-bound, mass-free
        // via the GearAndInventoryMass exclusion) plus zero or more Inventory
        // pickups (mass-bound under spareCap*0.95). Same Thing may appear in
        // both destinations within a trip. Drives our custom syncMetadata,
        // reReserve, branched pickup, and dual-unload toils.
        UwuCarryInventoryHybrid = 1,
    }

    // One trip: a sequence of pickups in the order the pawn should visit them.
    // All pickups in a trip happen before the pawn returns to the workbench.
    public class HaulTrip
    {
        public List<HaulPickup> Pickups;
    }

    // A single pickup: take Count units from Thing. Count may be less than the
    // stack's full size when the planner partial-takes a stack. Destination
    // determines which slot on the pawn the pickup is loaded into.
    public struct HaulPickup
    {
        public Thing Thing;
        public int Count;
        public PickupDestination Destination;
    }

    // Where a pickup is loaded on the pawn. Carry tracker is volume-bound (via
    // Pawn_CarryTracker.MaxStackSpaceEver) and lets the pawn over-carry on mass
    // at a movement-speed cost; inventory is mass-soft (no hard cap, but the
    // planner respects spare capacity to avoid stacking encumbrance penalties).
    // Default CarryTracker matches Sequential's one-stack-per-trip behavior so
    // existing planner output works unchanged.
    public enum PickupDestination
    {
        CarryTracker = 0,
        Inventory = 1,
    }
}

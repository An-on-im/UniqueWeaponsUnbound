namespace UniqueWeaponsUnbound.HaulPlanning
{
    // Computes a multi-trip pickup plan for a customization spec's ingredient
    // demand. Implementations are pure: given a request describing the pawn's
    // position and capacity, the workbench position, the demand, and a pool of
    // candidate stacks, they return a HaulPlan (or null if the demand can't be
    // met from the pool).
    //
    // Planners do not call into RimWorld globals (Find.*, listerThings, etc.).
    // Reservation, reach checks, and queue encoding are the caller's
    // responsibility — the planner is given a pool of already-validated
    // candidates and only chooses among them.
    public interface IHaulPlanner
    {
        // Returns a plan covering the request's demand using stacks from its
        // pool, or null if the demand cannot be satisfied.
        HaulPlan Plan(HaulPlanRequest request);

        // How much over-demand to size the candidate pool when the caller
        // gathers it. 1.0 means "exactly enough to meet demand"; higher values
        // give the planner more stacks to choose from at the cost of pool-build
        // time. Combined with CandidatePoolCap as an upper bound per def.
        float CandidatePoolMultiplier { get; }

        // Hard cap on the number of candidate stacks gathered per demanded
        // ThingDef, regardless of multiplier. Prevents pool-size blowup when a
        // def has many on-map stacks (e.g. Steel in a large colony). When
        // GroupPoolBySlotGroup is true this cap applies to distinct storage
        // groups instead of individual stacks.
        int CandidatePoolCap { get; }

        // When true, the caller's pool builder annotates each candidate with
        // the storage SlotGroup it sits in (HaulCandidate.GroupId) and applies
        // CandidatePoolMultiplier / CandidatePoolCap at the GROUP level: gather
        // nearest stacks until cumulative count reaches multiplier * demand AND
        // at least two distinct storage groups are represented (when the map
        // has that many), capped at CandidatePoolCap groups per def. The floor
        // matters: without it, one big stack satisfies the count target and a
        // sourcing optimizer receives exactly one candidate — no choice at all.
        // False keeps the legacy stack-level pool (Sweep / Sequential),
        // preserving their existing behavior.
        bool GroupPoolBySlotGroup { get; }
    }
}

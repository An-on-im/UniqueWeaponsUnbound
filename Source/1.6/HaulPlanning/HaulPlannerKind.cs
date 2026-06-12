namespace UniqueWeaponsUnbound.HaulPlanning
{
    /// <summary>
    /// Identifies the haul planner algorithm selected in mod settings. Stored
    /// in the settings file by name (Scribe), so renaming a value is a save
    /// migration; add new values at the end to preserve backward compatibility.
    /// </summary>
    public enum HaulPlannerKind
    {
        /// <summary>
        /// Original behavior: one stack picked up per trip, sorted by distance
        /// from the pawn. Always available; bedrock fallback for stub planners
        /// or planners that fail to produce a satisfying plan.
        /// </summary>
        Sequential = 0,

        /// <summary>
        /// Polar-sweep clustering around the workbench, bin-packed into trips
        /// up to encumbrance, with Held-Karp TSP within each trip. Default.
        /// </summary>
        Sweep = 1,

        /// <summary>
        /// Joint sourcing + trip-partition + routing optimization over
        /// storage-grouped candidates: minimal-cover support enumeration, one
        /// shared all-subsets Held-Karp table, and a subset-partition DP.
        /// Returns null above its tractability guards so the caller's ladder
        /// (Optimal -> Sweep -> Sequential) degrades to Sweep.
        /// </summary>
        Optimal = 2,
    }

    /// <summary>
    /// Resolves a HaulPlannerKind to a singleton planner instance. Planners
    /// are stateless, so a single instance per kind is reused across calls.
    /// </summary>
    public static class HaulPlannerFactory
    {
        private static readonly IHaulPlanner sequential = new SequentialHaulPlanner();
        private static readonly IHaulPlanner sweep = new SweepHaulPlanner();
        private static readonly IHaulPlanner optimal = new OptimalHaulPlanner();

        /// <summary>
        /// Returns the planner instance for the given kind. Unrecognized values
        /// fall back to Sequential — defensive against settings corruption or
        /// downgrading to an older mod version that doesn't know newer kinds.
        /// </summary>
        public static IHaulPlanner Get(HaulPlannerKind kind)
        {
            switch (kind)
            {
                case HaulPlannerKind.Sweep: return sweep;
                case HaulPlannerKind.Optimal: return optimal;
                case HaulPlannerKind.Sequential:
                default: return sequential;
            }
        }
    }
}

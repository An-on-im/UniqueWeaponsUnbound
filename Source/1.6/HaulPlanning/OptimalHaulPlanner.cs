using System;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /*
    ============================================================================
    OptimalHaulPlanner — algorithm specification (NOT YET IMPLEMENTED)
    ============================================================================

    PURPOSE
    -------
    Top-tier haul planner. Jointly optimizes the three decisions Sweep makes
    separately and greedily: sourcing (which stacks supply each demanded def),
    trip partitioning (which pickups share a round-trip), and in-trip routing
    (visit order). Exact over the dominant decision dimensions — see
    APPROXIMATIONS for the precise claim. The runtime budget is "imperceptible
    during the forcePause-protected dialog confirm": <50 ms worst case inside
    the tractability guards, single-digit milliseconds at expected sizes.

    Scope commitments:
      - No bespoke heuristic path. When a tractability guard trips, Plan()
        returns null and the caller's degradation ladder (IngredientReservation:
        Optimal -> Sweep -> Sequential) rebuilds the pool with Sweep's own
        parameters and runs Sweep. One battle-tested fallback, zero duplicated
        heuristic machinery.
      - PawnPosition is deliberately ignored. The customization flow opens the
        dialog with the pawn already at the workbench, so the depot and the
        pawn's origin coincide; modelling a distinct first-trip origin buys
        nothing for real inputs.

    DESIGN INSIGHTS (why this shape)
    --------------------------------
    1. Counts are cost-irrelevant. Every stack of a demanded def shares one
       unit mass, so the total hauled mass per def is R_d * u_d regardless of
       which stacks supply it, and a trip's walking cost depends only on WHICH
       POSITIONS it visits. Per-stack counts never change cost — they only
       redistribute mass between trips (a feasibility concern). The planner
       therefore enumerates SUPPORTS (which sources supply each def), never
       count vectors, and derives counts canonically per support. (An earlier
       draft of this spec enumerated integer count vectors: ~C(35,5) = 324,632
       options for a single def at R=30 over 6 stacks. Removed.)

    2. All subset tour costs come from ONE Held-Karp table. Define
       dp[mask][i] = cheapest path that leaves the workbench, visits exactly
       the positions in mask, and ends at position i. Built once over all
       unique candidate positions in O(2^M * M^2), it yields the closed-tour
       cost of ANY position subset as min_i dp[mask][i] + dist(i, WB). No
       per-subset solves, no cross-sourcing cost cache. (The earlier draft ran
       Held-Karp independently per subset: sum_k C(15,k) * k^2 * 2^k ~ 1.5e9
       ops per sourcing. The table is ~7.4e6 ops total at M=15.)

    3. Storage grouping collapses the node space. Stacks of one def inside the
       same SlotGroup (stockpile zone, storage building, shelf) are a single
       sourcing decision in practice — the player thinks "take it from the
       fridge", not "take stack #3". Merging them into one node shrinks N to
       roughly (defs x storage sites), which keeps real colonies far inside
       the exact path's guards. Same-position dedupe in the TSP table also
       covers ungrouped same-cell stacks (e.g. ground clutter) for free.

    POOL CONTRACT (requires pool-builder work in IngredientReservation)
    -------------------------------------------------------------------
    New IHaulPlanner property: GroupPoolBySlotGroup (default false; true here).
    Sweep and Sequential keep the default, so their pool behavior — and
    Sequential's vanilla-equivalence — is untouched.

    When true, BuildHaulPool:
      - Annotates each HaulCandidate with a GroupId snapshotted at pool-build
        time via map.haulDestinationManager.SlotGroupAt(position). -1 for
        stacks outside storage (each becomes a singleton group). This respects
        planner purity: the planner itself never queries world state.
      - Applies CandidatePoolMultiplier / CandidatePoolCap at the GROUP level:
        gather nearest stacks until cumulative count >= R_d * 2.0 AND at least
        2 distinct groups are represented (when the map has 2+), capped at 8
        groups per def. The floor matters: without it, one 75-unit stack
        satisfies a 30-unit demand's count target and the optimizer receives
        exactly one candidate — no sourcing choice at all.
    Do NOT key on StorageGroup (linked storage settings) — linked buildings
    can span the map and share no locality. SlotGroup is the locality unit.

    NODE MODEL
    ----------
    Node = (def, group): member stacks sorted nearest-to-workbench, aggregate
    available count, unit mass, representative position = nearest member's
    cell. Groups whose bounding box exceeds ~8 cells on either axis (sprawling
    or non-contiguous stockpile zones) are split by recursive bisection along
    the wider axis until compliant, bounding the representative-position error.

    Unique positions are indexed separately from nodes: distinct defs stored
    in the same room are distinct nodes sharing one position bit, as are
    virtual copies (below). posMaskOf[nodeMask] (incrementally computed) maps
    partition-DP masks to TSP-table masks, so co-located nodes in one trip
    are never double-walked, and a position revisited by two trips is paid
    once per trip — exactly right.

    ALGORITHM
    ---------
    Phase 1 — Nodes: build nodes from the grouped pool as above.

    Phase 2 — TSP table: one Held-Karp pass over the M unique positions
        (Manhattan distance, int costs). tour(posMask) = min_i dp[posMask][i]
        + dWB[i]. No parent tables here; final trips are re-sequenced with the
        shared small solver in Phase 5.

    Phase 3 — Support enumeration, per def: enumerate MINIMAL covers of R_d
        over the def's groups (every member group necessary — by minimality
        each carries a positive count under any exact assignment). Canonical
        counts: nearest-to-workbench groups take their full available count,
        the marginal group takes the remainder; the same nearest-first rule
        canonicalizes member-stack takes inside each group. Cap minimal covers
        per def (~12, nearest-biased) before taking the cross-def Cartesian
        product of supports.

    Phase 4 — Per support combo U (union of per-def supports):
        Pre-split: any node whose canonical take can't fit one trip even with
        the best carry-tracker bypass is split into virtual copy nodes with
        chunk takes that fit (same position — copies in one trip cost nothing
        extra via posMaskOf; copies in different trips correctly re-pay the
        walk). This is what lets one oversized stack legally span trips — the
        case the earlier draft returned null on and Sweep handled.

        Partition DP over node masks, lowbit-pinned so each partition is
        enumerated once:
            f[mask] = min over T subset-of mask, T containing lowbit(mask),
                      feasible(T):  tour(posMaskOf[T]) + f[mask \ T]
            f[0] = 0; parent[mask] = argmin T for reconstruction.
        feasible(T): invMass(T) = sum(take_n * u_n) - bypass(T) <= B, where
        bypass(T) = max over member STACKS s in T of min(take_s, ctVol(def_s))
        * u_s — the carry-tracker designation. Maximizing bypassed mass is
        dominant (bypass only ever relaxes the constraint and never affects
        tour cost), so greedy CT choice loses nothing. Bypass is computed per
        stack, never per group: the carry tracker holds one stack. When the
        designated stack's take exceeds ctVol, the overflow rides as an
        Inventory pickup of the same Thing in the same trip (Sweep's split
        rule).

    Phase 5 — Answer: argmin of f[full(U)] across combos; walk parents to
        recover trips; expand each node to member-stack pickups (canonical
        takes); sequence each trip's unique positions with the shared
        Held-Karp solver, keeping co-located pickups adjacent; emit
        Destination on EVERY pickup and ExecutionStrategy =
        UwuCarryInventoryHybrid. (Unset Destination is read as CarryTracker —
        the legacy default — which is only correct for one-stack trips.)

    CAPACITY MODEL
    --------------
    B = (CapacityKg - CurrentEncumbranceKg) * CapacityFactor (0.95, shared
    constant with Sweep). CurrentEncumbranceKg is gear + inventory
    (MassUtility.GearAndInventoryMass): pre-existing inventory stays on the
    pawn across every trip, so it occupies budget the whole time.
      - B <= 0: return null (matches Sweep; the Sequential rung hauls via
        carry tracker with no mass cap, so the ladder still succeeds).
      - Massless defs (u = 0): no capacity pressure; always feasible.
      - ctVol = MaxStackSpaceEver(def, CapacityKg) (shared helper); a def
        with ctVol = 0 simply contributes no bypass.

    TRACTABILITY GUARDS (all -> return null -> ladder runs Sweep)
    -------------------------------------------------------------
      - Nodes (including virtual copies) > 15, or unique positions > 15.
      - Virtual copies required for a single node > 4.
      - Upfront work estimate sum-over-combos of 3^|U| > ~30M elementary DP
        steps (computable from |U| per combo before running anything).
    Guards are deterministic counts, never wall-clock: identical requests must
    produce identical plans on every machine (multiplayer support is under
    consideration, and a timing-based abort would desync). Log once at info
    level when a guard trips so real-world frequency is observable.

    APPROXIMATIONS (accepted, documented)
    -------------------------------------
    Exact over: which groups supply each def x how nodes partition into trips
    x carry-tracker designation x in-trip visit order. Approximate in:
      - Canonical count split within a support (mass distribution only; in
        rare tight-capacity cases a different split would admit a partition
        this one rejects).
      - Group representative position (error bounded by the ~8-cell span
        guard; small against cross-map trip lengths).
      - Symmetric virtual-copy permutations are explored redundantly (wasted
        work only, never wrong answers).
      - Manhattan distance ignores walls/terrain — same simplification as
        Sweep, same justification.

    EDGE CASES & DEFENSIVE BEHAVIOR
    -------------------------------
      - Empty demand: empty plan with hybrid strategy (mirror Sweep).
      - Missing def in pool, or coverage impossible (sum of availability
        < R_d): return null.
      - B <= 0: return null (see CAPACITY MODEL).
      - Any guard trips: return null. The planner never throws for size.

    SHARED COMPONENTS
    -----------------
    Extract from SweepHaulPlanner into HaulPlanning.Internal and share:
      - Held-Karp: the all-subsets table builder (new, this planner) AND the
        single-trip Solve used by both planners for final sequencing. Costs
        stay int (Manhattan is integral; float ties would threaten
        determinism).
      - ManhattanDist, MaxStackSpaceEver, CapacityFactor (0.95), MassEpsilon.
    Sweep also currently feeds a CT/inventory split of one stack to Held-Karp
    as two same-position nodes; route both planners through position-level
    dedupe in the shared solver.

    COMPLEXITY & BUDGETS
    --------------------
      - TSP table: O(2^M * M^2) — 16K ops at M=8, 7.4M at the M=15 guard.
      - Partition DPs: sum of 3^|U| across combos, capped at ~30M; typical
        colonies (1-3 storage sites per def, |U| <= 8) are under 100K.
      - Support enumeration, expansion, reconstruction: noise.
    Worst case inside the guards ~40M elementary ops — well under 50 ms;
    typical instances well under 5 ms. Transient memory peaks at the dp table:
    2^M * M ints = ~2 MB at M=15, ~8 KB at M=8.

    TESTING NOTES
    -------------
    Same harness as SweepHaulPlanner, plus:
      - Brute-force oracle for n <= 6: enumerate ALL plans — including ones
        that split a stack across trips — and assert the planner matches the
        optimum. (Hand-computed cases can't cover the split-stack space; the
        earlier draft's gap there would have been caught by this oracle.)
      - Comparative property: Optimal's plan cost <= Sweep's plan cost on the
        same request, on fixtures whose group spans are small (the
        representative-position error bound is the tolerance).
      - Contract invariants: per-def pickup totals exactly equal demand;
        per-stack takes <= snapshot AvailableCount; Destination set on every
        pickup; ExecutionStrategy = UwuCarryInventoryHybrid.
      - Determinism: identical requests yield identical plans across repeated
        runs.
      - Guard behavior: each guard trips -> Plan() returns null (delegation is
        the caller's, so null IS the planner's contract).
      - Pool grouping: same-SlotGroup stacks merge; span guard splits
        sprawling zones; GroupId -1 stacks stay singletons.
      - Performance asserts use an op-count instrument, not wall-clock — the
        Windows CI runner makes timing asserts flaky.

    REFERENCES
    ----------
    - Held-Karp DP: Held, M. & Karp, R. (1962). "A Dynamic Programming
      Approach to Sequencing Problems." Journal of SIAM, 10(1).
    - Partition-into-subsets DP over a precomputed subset-cost table:
      standard exponential set-partition pattern; see Bellman (1962),
      "Dynamic programming treatment of the travelling salesman problem."
    - RimWorld storage locality: StoreUtility.GetSlotGroup(Thing) /
      Map.haulDestinationManager.SlotGroupAt(IntVec3), snapshotted at
      pool-build time only.

    ============================================================================
    */
    public class OptimalHaulPlanner : IHaulPlanner
    {
        // Group-level semantics once GroupPoolBySlotGroup lands in the pool
        // builder: gather until 2x demand AND >= 2 distinct storage groups,
        // capped at 8 groups per def. Stack-level until then.
        public float CandidatePoolMultiplier => 2.0f;

        public int CandidatePoolCap => 8;

        public HaulPlan Plan(HaulPlanRequest request)
        {
            // STUB: see class-level comment for the full algorithm spec.
            throw new NotImplementedException(
                "OptimalHaulPlanner is not yet implemented. See the class-level "
                + "comment in OptimalHaulPlanner.cs for the full algorithm spec.");
        }
    }
}

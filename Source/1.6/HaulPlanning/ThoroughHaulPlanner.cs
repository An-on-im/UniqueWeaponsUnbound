using System;
using System.Collections.Generic;
using UniqueWeaponsUnbound.HaulPlanning.Internal;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning
{
    /*
    ============================================================================
    ThoroughHaulPlanner — algorithm specification and design rationale
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
        Thorough -> Sweep -> Sequential) rebuilds the pool with Sweep's own
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

    POOL CONTRACT (implemented by IngredientReservation.BuildHaulPool)
    -------------------------------------------------------------------
    IHaulPlanner property GroupPoolBySlotGroup (false elsewhere; true here).
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

        Pre-split (computed here, per support — a node's canonical take
        depends only on its own def's cover, so it need not wait for the
        cross-def combo): any node whose canonical take can't fit one trip
        even with the best carry-tracker bypass is split into virtual copy
        nodes with chunk takes that fit (same position — copies in one trip
        cost nothing extra via posMaskOf; copies in different trips correctly
        re-pay the walk). This is what lets one oversized stack legally span
        trips — the case the earlier draft returned null on and Sweep handled.

    Phase 4 — Per support combo U (union of per-def supports):
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
        steps (computable from |U| per combo before running anything; the sum
        factors as product-over-defs of per-def sums, so no combo enumeration
        is needed to evaluate it).
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

    SHARED COMPONENTS (HaulPlanning.Internal, extracted from Sweep)
    ---------------------------------------------------------------
      - HeldKarp: the all-subsets table builder (SubsetTourTable) AND the
        single-trip Cost/Order used by both planners for final sequencing.
        Costs stay int (Manhattan is integral; float ties would threaten
        determinism). Both planners route through position-level dedupe in
        the shared solver (a CT/inventory split of one stack is one node).
      - HaulMath: ManhattanDist, MaxStackSpaceEver, CapacityFactor (0.95),
        MassEpsilon.

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
      - Comparative property: Thorough's plan cost <= Sweep's plan cost on the
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

    /// <summary>
    /// Top-tier planner: jointly optimizes sourcing, trip partitioning, and
    /// in-trip routing over storage-grouped candidates — minimal-cover
    /// support enumeration, one shared all-subsets Held-Karp table, and a
    /// subset-partition DP. Exact over the dominant decision dimensions (see
    /// the spec comment above); returns null when a tractability guard trips
    /// so the caller's ladder degrades to Sweep.
    /// </summary>
    public class ThoroughHaulPlanner : IHaulPlanner
    {
        // Group-level pool sizing (GroupPoolBySlotGroup): gather nearest
        // stacks until cumulative count >= 2x demand AND >= 2 distinct
        // storage groups, capped at 8 groups per def.
        public float CandidatePoolMultiplier => 2.0f;

        public int CandidatePoolCap => 8;

        public bool GroupPoolBySlotGroup => true;

        // Tractability guards — deterministic counts, never wall-clock.
        private const int MaxNodes = 15;
        private const int MaxPositions = 15;
        private const int MaxVirtualCopiesPerNode = 4;
        private const long MaxPartitionDpSteps = 30_000_000;
        private const int MaxCoversPerDef = 12;
        private const int MaxGroupSpan = 8;

        // Cost sentinel for unreachable partition-DP states; small enough
        // that adding a tour cost can never overflow int.
        private const int Infeasible = int.MaxValue / 4;

        /// <summary>
        /// Elementary partition-DP steps consumed by the most recent Plan()
        /// call. Diagnostics only (op-count performance asserts in tests —
        /// wall-clock asserts are flaky on CI); planner behavior never reads
        /// it, so the planner stays effectively stateless.
        /// </summary>
        internal static long LastPlanPartitionSteps;

        /// <summary>
        /// Test kill-switch for guard-trip logging: Verse.Log isn't usable
        /// under the bare xUnit runner. Always true in game.
        /// </summary>
        internal static bool EmitGuardLogs = true;

        public HaulPlan Plan(HaulPlanRequest request)
        {
            LastPlanPartitionSteps = 0;

            if (request.Demand == null || request.Demand.Count == 0)
                return EmptyPlan();
            if (request.Pool == null)
                return null;

            float budget = (request.CapacityKg - request.CurrentEncumbranceKg)
                * HaulMath.CapacityFactor;
            if (budget <= 0f)
                return null;

            IntVec3 wb = request.WorkbenchPosition;

            // Phase 1 — nodes.
            List<DefNodes> defs = BuildNodes(request, wb);
            if (defs == null)
                return null;
            if (defs.Count == 0)
                return EmptyPlan();

            int totalNodes = 0;
            foreach (DefNodes d in defs)
                totalNodes += d.Nodes.Count;
            if (totalNodes > MaxNodes)
                return GuardTripped("node count " + totalNodes + " > " + MaxNodes);

            // Unique positions <= nodes (positions are node representatives),
            // so this can't trip past the node guard — kept as a cheap
            // explicit invariant for the TSP table's 2^M allocation.
            List<IntVec3> positions = IndexPositions(defs);
            if (positions.Count > MaxPositions)
                return GuardTripped("unique positions " + positions.Count + " > " + MaxPositions);

            // Phase 2 — one all-subsets tour table.
            var tours = new SubsetTourTable(positions, wb);

            // Phase 3 — per-def supports.
            var supports = new List<List<Support>>(defs.Count);
            foreach (DefNodes d in defs)
            {
                List<Support> defSupports = EnumerateSupports(d, budget, out bool copyGuard);
                if (defSupports == null)
                {
                    return copyGuard
                        ? GuardTripped("a node needs more than "
                            + MaxVirtualCopiesPerNode + " virtual copies")
                        : null; // a unit of this def can't ride any trip
                }
                supports.Add(defSupports);
            }

            // Guard the node-mask width first (virtual copies can push a
            // combo past the base node count), then the upfront work
            // estimate: sum-over-combos of 3^|U| = product-over-defs of
            // per-def sums of 3^k, with early-out before overflow.
            int maxComboNodes = 0;
            foreach (List<Support> defSupports in supports)
            {
                int maxForDef = 0;
                foreach (Support s in defSupports)
                    maxForDef = Math.Max(maxForDef, s.Nodes.Count);
                maxComboNodes += maxForDef;
            }
            if (maxComboNodes > MaxNodes)
                return GuardTripped("combo node count " + maxComboNodes
                    + " > " + MaxNodes + " with virtual copies");

            long work = 1;
            foreach (List<Support> defSupports in supports)
            {
                long defSum = 0;
                foreach (Support s in defSupports)
                    defSum += Pow3(s.Nodes.Count);
                work *= defSum;
                if (work > MaxPartitionDpSteps)
                    return GuardTripped("work estimate over "
                        + MaxPartitionDpSteps + " partition-DP steps");
            }

            // Phase 4 — partition DP per support combo, tracking the global
            // argmin. Strict less-than keeps the first-found best, and combos
            // enumerate in deterministic odometer order over defName-sorted
            // defs, so ties never depend on machine or run.
            var buffers = new PartitionBuffers(maxComboNodes);
            var comboNodes = new List<PlannedNode>(maxComboNodes);
            var choice = new int[supports.Count];
            int bestCost = int.MaxValue;
            List<int> bestTripMasks = null;
            List<PlannedNode> bestNodes = null;

            while (true)
            {
                comboNodes.Clear();
                for (int i = 0; i < supports.Count; i++)
                    comboNodes.AddRange(supports[i][choice[i]].Nodes);

                int cost = SolvePartition(comboNodes, tours, budget, buffers,
                    out List<int> tripMasks);
                if (tripMasks != null && cost < bestCost)
                {
                    bestCost = cost;
                    bestTripMasks = tripMasks;
                    bestNodes = new List<PlannedNode>(comboNodes);
                }

                int pos = supports.Count - 1;
                while (pos >= 0)
                {
                    choice[pos]++;
                    if (choice[pos] < supports[pos].Count) break;
                    choice[pos] = 0;
                    pos--;
                }
                if (pos < 0) break;
            }

            // Unreachable in practice — pre-split guarantees every singleton
            // trip is feasible — but never emit a half-formed plan.
            if (bestTripMasks == null)
                return null;

            // Phase 5 — expand, sequence, emit.
            return Emit(bestNodes, bestTripMasks, wb);
        }

        // ------------------------------------------------------------------
        // Phase 1 — node construction
        // ------------------------------------------------------------------

        private sealed class DefNodes
        {
            public int Demand;
            public List<Node> Nodes;
        }

        // One sourcing decision: a def's stacks within one storage group
        // (possibly bisected by the span guard), or a lone stack.
        private sealed class Node
        {
            public int CtVol;
            public List<Member> Members; // sorted nearest-to-workbench
            public int Available;
            public IntVec3 RepPos;       // nearest member's cell
            public int DistWB;
            public int PosIndex;         // into the unique-position table
        }

        private struct Member
        {
            public Thing Thing;
            public IntVec3 Position;
            public int Available;
            public float UnitMass;
        }

        private static List<DefNodes> BuildNodes(HaulPlanRequest request, IntVec3 wb)
        {
            // Sort demanded defs by name so Dictionary iteration order (an
            // implementation detail) can never change the plan.
            var defOrder = new List<ThingDef>(request.Demand.Keys);
            defOrder.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));

            var result = new List<DefNodes>();
            foreach (ThingDef def in defOrder)
            {
                int needed = request.Demand[def];
                if (needed <= 0)
                    continue;

                if (!request.Pool.TryGetValue(def, out List<HaulCandidate> candidates)
                    || candidates == null
                    || candidates.Count == 0)
                {
                    return null;
                }

                int ctVol = HaulMath.MaxStackSpaceEver(def, request.CapacityKg);

                // Bucket members by GroupId in pool order; negative ids are
                // singleton groups.
                var memberGroups = new List<List<Member>>();
                var groupIndexOf = new Dictionary<int, int>();
                int available = 0;
                foreach (HaulCandidate c in candidates)
                {
                    if (c.Thing == null || c.AvailableCount <= 0)
                        continue;
                    var member = new Member
                    {
                        Thing = c.Thing,
                        Position = c.Position,
                        Available = c.AvailableCount,
                        UnitMass = Mathf.Max(c.MassPerUnit, 0f),
                    };
                    if (c.GroupId < 0)
                    {
                        memberGroups.Add(new List<Member> { member });
                    }
                    else if (groupIndexOf.TryGetValue(c.GroupId, out int at))
                    {
                        memberGroups[at].Add(member);
                    }
                    else
                    {
                        groupIndexOf[c.GroupId] = memberGroups.Count;
                        memberGroups.Add(new List<Member> { member });
                    }
                    available += c.AvailableCount;
                }
                if (available < needed)
                    return null;

                var nodes = new List<Node>();
                foreach (List<Member> members in memberGroups)
                    AddNodesSplitBySpan(members, ctVol, wb, nodes);

                // Nearest-first node order — the canonical-count rule and the
                // cover bias both key off it.
                nodes.Sort((a, b) =>
                {
                    int c = a.DistWB.CompareTo(b.DistWB);
                    if (c != 0) return c;
                    c = a.RepPos.x.CompareTo(b.RepPos.x);
                    if (c != 0) return c;
                    return a.RepPos.z.CompareTo(b.RepPos.z);
                });

                result.Add(new DefNodes { Demand = needed, Nodes = nodes });
            }
            return result;
        }

        // Span guard: a single SlotGroup can sprawl (snaking stockpiles,
        // zones split across rooms), and one representative position would
        // misprice it. Bisect along the wider axis until each part's bounding
        // box fits MaxGroupSpan on both axes. Both halves are always
        // non-empty: span > MaxGroupSpan >= 1 puts the midpoint strictly
        // between the extremes.
        private static void AddNodesSplitBySpan(
            List<Member> members, int ctVol, IntVec3 wb, List<Node> nodes)
        {
            int minX = int.MaxValue, maxX = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (Member m in members)
            {
                minX = Math.Min(minX, m.Position.x);
                maxX = Math.Max(maxX, m.Position.x);
                minZ = Math.Min(minZ, m.Position.z);
                maxZ = Math.Max(maxZ, m.Position.z);
            }

            if (maxX - minX <= MaxGroupSpan && maxZ - minZ <= MaxGroupSpan)
            {
                nodes.Add(MakeNode(members, ctVol, wb));
                return;
            }

            bool splitX = maxX - minX >= maxZ - minZ;
            int mid = splitX ? (minX + maxX) / 2 : (minZ + maxZ) / 2;
            var lo = new List<Member>();
            var hi = new List<Member>();
            foreach (Member m in members)
            {
                int coord = splitX ? m.Position.x : m.Position.z;
                (coord <= mid ? lo : hi).Add(m);
            }
            AddNodesSplitBySpan(lo, ctVol, wb, nodes);
            AddNodesSplitBySpan(hi, ctVol, wb, nodes);
        }

        private static Node MakeNode(List<Member> members, int ctVol, IntVec3 wb)
        {
            members.Sort((a, b) =>
            {
                int c = HaulMath.ManhattanDist(a.Position, wb)
                    .CompareTo(HaulMath.ManhattanDist(b.Position, wb));
                if (c != 0) return c;
                c = a.Position.x.CompareTo(b.Position.x);
                if (c != 0) return c;
                return a.Position.z.CompareTo(b.Position.z);
            });

            int available = 0;
            foreach (Member m in members)
                available += m.Available;

            return new Node
            {
                CtVol = ctVol,
                Members = members,
                Available = available,
                RepPos = members[0].Position,
                DistWB = HaulMath.ManhattanDist(members[0].Position, wb),
            };
        }

        private static List<IntVec3> IndexPositions(List<DefNodes> defs)
        {
            var positions = new List<IntVec3>();
            var indexOf = new Dictionary<IntVec3, int>();
            foreach (DefNodes d in defs)
            {
                foreach (Node n in d.Nodes)
                {
                    if (!indexOf.TryGetValue(n.RepPos, out int at))
                    {
                        at = positions.Count;
                        indexOf[n.RepPos] = at;
                        positions.Add(n.RepPos);
                    }
                    n.PosIndex = at;
                }
            }
            return positions;
        }

        // ------------------------------------------------------------------
        // Phase 3 — support enumeration
        // ------------------------------------------------------------------

        // One node carrying a concrete canonical take — possibly one chunk of
        // a virtual-copy split. The unit of the partition DP.
        private sealed class PlannedNode
        {
            public Node Source;
            public List<MemberTake> Takes;
            public float Mass;       // sum of take * unitMass
            public float BestBypass; // max over takes of min(take, ctVol) * unitMass
            public int PosIndex;
        }

        private struct MemberTake
        {
            public Thing Thing;
            public IntVec3 Position;
            public int Count;
            public float UnitMass;
        }

        private sealed class Support
        {
            public List<PlannedNode> Nodes;
        }

        // Enumerates minimal covers of the def's demand over its nodes,
        // canonicalizes counts, and pre-splits oversized takes into virtual
        // copies. Returns null with copyGuard=true when a node would need
        // more than MaxVirtualCopiesPerNode chunks, and null with
        // copyGuard=false when a single unit can't ride any trip (genuinely
        // unfittable — same silent null as Sweep's bin-pack).
        private static List<Support> EnumerateSupports(
            DefNodes d, float budget, out bool copyGuard)
        {
            copyGuard = false;
            List<Node> nodes = d.Nodes;
            int k = nodes.Count;
            int size = 1 << k;

            // Aggregate availability per subset (lowbit-incremental), then
            // keep MINIMAL covers: drop any one member and the rest no longer
            // covers demand.
            var availOf = new int[size];
            for (int mask = 1; mask < size; mask++)
            {
                int low = mask & -mask;
                availOf[mask] = availOf[mask ^ low] + nodes[IndexOfLowBit(low)].Available;
            }

            var covers = new List<CoverKey>();
            for (int mask = 1; mask < size; mask++)
            {
                if (availOf[mask] < d.Demand)
                    continue;
                bool minimal = true;
                int sumDist = 0;
                int count = 0;
                for (int i = 0; i < k; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    if (availOf[mask] - nodes[i].Available >= d.Demand)
                    {
                        minimal = false;
                        break;
                    }
                    sumDist += nodes[i].DistWB;
                    count++;
                }
                if (minimal)
                    covers.Add(new CoverKey { Mask = mask, SumDist = sumDist, Count = count });
            }

            // Nearest-biased cap; the mask tiebreak makes the order total, so
            // capping is deterministic.
            covers.Sort((a, b) =>
            {
                int c = a.SumDist.CompareTo(b.SumDist);
                if (c != 0) return c;
                c = a.Count.CompareTo(b.Count);
                if (c != 0) return c;
                return a.Mask.CompareTo(b.Mask);
            });

            int keep = Math.Min(covers.Count, MaxCoversPerDef);
            var supports = new List<Support>(keep);
            for (int i = 0; i < keep; i++)
            {
                Support s = BuildSupport(d, covers[i].Mask, budget, out copyGuard);
                if (s == null)
                    return null;
                supports.Add(s);
            }
            return supports;
        }

        private struct CoverKey
        {
            public int Mask;
            public int SumDist;
            public int Count;
        }

        // Canonical counts for one minimal cover: iterate the cover's nodes
        // nearest-first; each takes its full availability until the marginal
        // (farthest) node takes the remainder. Minimality guarantees every
        // member ends up with a positive take. The same nearest-first rule
        // canonicalizes member-stack takes inside each node.
        private static Support BuildSupport(
            DefNodes d, int mask, float budget, out bool copyGuard)
        {
            copyGuard = false;
            var planned = new List<PlannedNode>();
            int remaining = d.Demand;
            for (int i = 0; i < d.Nodes.Count && remaining > 0; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                Node node = d.Nodes[i];
                int nodeTake = Math.Min(remaining, node.Available);
                remaining -= nodeTake;

                var takes = new List<MemberTake>();
                int left = nodeTake;
                for (int j = 0; j < node.Members.Count && left > 0; j++)
                {
                    Member m = node.Members[j];
                    int take = Math.Min(left, m.Available);
                    takes.Add(new MemberTake
                    {
                        Thing = m.Thing,
                        Position = m.Position,
                        Count = take,
                        UnitMass = m.UnitMass,
                    });
                    left -= take;
                }

                List<PlannedNode> chunks = ChunkTake(node, takes, budget, out bool unfittable);
                if (chunks == null)
                {
                    copyGuard = !unfittable;
                    return null;
                }
                planned.AddRange(chunks);
            }
            return new Support { Nodes = planned };
        }

        // Pre-split into virtual copies: greedy nearest-first maximal chunks,
        // each feasible as a solo trip. Returns null when the take needs more
        // than MaxVirtualCopiesPerNode chunks (unfittable=false: copy guard)
        // or when even a single unit can't ride any trip (unfittable=true).
        private static List<PlannedNode> ChunkTake(
            Node node, List<MemberTake> takes, float budget, out bool unfittable)
        {
            unfittable = false;
            var chunks = new List<PlannedNode>();
            var cur = new List<MemberTake>();
            float curMass = 0f;
            float curByp = 0f;

            foreach (MemberTake take in takes)
            {
                int remaining = take.Count;
                while (remaining > 0)
                {
                    int x = MaxAddable(curMass, curByp, take.UnitMass, node.CtVol,
                        remaining, budget);
                    if (x <= 0)
                    {
                        if (cur.Count == 0)
                        {
                            unfittable = true;
                            return null;
                        }
                        // Closing this chunk leaves at least one more, so the
                        // total would exceed the copy guard.
                        if (chunks.Count == MaxVirtualCopiesPerNode - 1)
                            return null;
                        chunks.Add(MakePlannedNode(node, cur, curMass, curByp));
                        cur = new List<MemberTake>();
                        curMass = 0f;
                        curByp = 0f;
                        continue;
                    }
                    cur.Add(new MemberTake
                    {
                        Thing = take.Thing,
                        Position = take.Position,
                        Count = x,
                        UnitMass = take.UnitMass,
                    });
                    curMass += x * take.UnitMass;
                    curByp = Math.Max(curByp, Math.Min(x, node.CtVol) * take.UnitMass);
                    remaining -= x;
                }
            }
            if (cur.Count > 0)
                chunks.Add(MakePlannedNode(node, cur, curMass, curByp));
            return chunks;
        }

        private static PlannedNode MakePlannedNode(
            Node node, List<MemberTake> takes, float mass, float bestBypass)
        {
            return new PlannedNode
            {
                Source = node,
                Takes = takes,
                Mass = mass,
                BestBypass = bestBypass,
                PosIndex = node.PosIndex,
            };
        }

        // Largest count of one member addable to a chunk while the chunk
        // stays feasible as a solo trip. invMass(x) = mass + x*u -
        // max(byp, min(x, ctVol)*u) is monotonically non-decreasing in x
        // (the bypass grows at most as fast as the mass), so binary search
        // on the feasibility predicate is exact.
        private static int MaxAddable(
            float mass, float byp, float u, int ctVol, int limit, float budget)
        {
            if (u <= 0f)
                return limit; // massless: no capacity pressure
            if (FitsSolo(mass, byp, u, ctVol, limit, budget))
                return limit;
            if (!FitsSolo(mass, byp, u, ctVol, 1, budget))
                return 0;
            int lo = 1, hi = limit; // invariant: Fits(lo), !Fits(hi)
            while (hi - lo > 1)
            {
                int mid = lo + (hi - lo) / 2;
                if (FitsSolo(mass, byp, u, ctVol, mid, budget)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        private static bool FitsSolo(
            float mass, float byp, float u, int ctVol, int x, float budget)
        {
            float bypass = Math.Max(byp, Math.Min(x, ctVol) * u);
            return mass + x * u - bypass <= budget + HaulMath.MassEpsilon;
        }

        // ------------------------------------------------------------------
        // Phase 4 — partition DP
        // ------------------------------------------------------------------

        // Scratch arrays reused across combos, sized once to the largest
        // combo — per-combo allocation would churn the GC at high combo
        // counts for no benefit.
        private sealed class PartitionBuffers
        {
            public readonly int[] PosMask;
            public readonly float[] Mass;
            public readonly float[] Byp;
            public readonly int[] F;
            public readonly int[] Parent;

            public PartitionBuffers(int maxNodes)
            {
                int size = 1 << Math.Max(maxNodes, 1);
                PosMask = new int[size];
                Mass = new float[size];
                Byp = new float[size];
                F = new int[size];
                Parent = new int[size];
            }
        }

        // f[mask] = cheapest trip partition of the node subset `mask`, with
        // the first trip pinned to contain lowbit(mask) so each partition is
        // enumerated exactly once. Subset sums (position mask, mass, best
        // bypass) are lowbit-incremental, so feasibility and tour lookup per
        // candidate trip are O(1).
        private static int SolvePartition(
            List<PlannedNode> nodes, SubsetTourTable tours, float budget,
            PartitionBuffers buf, out List<int> tripMasks)
        {
            int k = nodes.Count;
            int size = 1 << k;
            int[] posMaskOf = buf.PosMask;
            float[] massOf = buf.Mass;
            float[] bypOf = buf.Byp;
            int[] f = buf.F;
            int[] parent = buf.Parent;

            posMaskOf[0] = 0;
            massOf[0] = 0f;
            bypOf[0] = 0f;
            for (int mask = 1; mask < size; mask++)
            {
                int low = mask & -mask;
                int li = IndexOfLowBit(low);
                int rest = mask ^ low;
                posMaskOf[mask] = posMaskOf[rest] | (1 << nodes[li].PosIndex);
                massOf[mask] = massOf[rest] + nodes[li].Mass;
                bypOf[mask] = Math.Max(bypOf[rest], nodes[li].BestBypass);
            }

            long steps = 0;
            f[0] = 0;
            for (int mask = 1; mask < size; mask++)
            {
                int low = mask & -mask;
                int rest = mask ^ low;
                int best = Infeasible;
                int bestT = 0;
                int sub = rest;
                while (true)
                {
                    int t = sub | low;
                    steps++;
                    if (massOf[t] - bypOf[t] <= budget + HaulMath.MassEpsilon)
                    {
                        int c = f[mask ^ t];
                        if (c < Infeasible)
                        {
                            c += tours.TourCost(posMaskOf[t]);
                            if (c < best)
                            {
                                best = c;
                                bestT = t;
                            }
                        }
                    }
                    if (sub == 0) break;
                    sub = (sub - 1) & rest;
                }
                f[mask] = best;
                parent[mask] = bestT;
            }
            LastPlanPartitionSteps += steps;

            int full = size - 1;
            if (f[full] >= Infeasible)
            {
                tripMasks = null;
                return int.MaxValue;
            }
            tripMasks = new List<int>();
            for (int mask = full; mask != 0; mask ^= parent[mask])
            {
                if (parent[mask] == 0)
                {
                    tripMasks = null; // defensive: corrupt parent chain
                    return int.MaxValue;
                }
                tripMasks.Add(parent[mask]);
            }
            return f[full];
        }

        // ------------------------------------------------------------------
        // Phase 5 — emission
        // ------------------------------------------------------------------

        private struct TripTake
        {
            public Thing Thing;
            public IntVec3 Position;
            public int Count;
            public float UnitMass;
            public int CtVol;
        }

        private static HaulPlan Emit(
            List<PlannedNode> nodes, List<int> tripMasks, IntVec3 wb)
        {
            var trips = new List<HaulTrip>(tripMasks.Count);
            foreach (int mask in tripMasks)
            {
                // Merge same-stack slices: virtual copies of one node landing
                // in the same trip re-join into a single pickup.
                var takes = new List<TripTake>();
                var indexOf = new Dictionary<Thing, int>();
                for (int i = 0; i < nodes.Count; i++)
                {
                    if ((mask & (1 << i)) == 0)
                        continue;
                    PlannedNode pn = nodes[i];
                    foreach (MemberTake t in pn.Takes)
                    {
                        if (indexOf.TryGetValue(t.Thing, out int at))
                        {
                            TripTake merged = takes[at];
                            merged.Count += t.Count;
                            takes[at] = merged;
                        }
                        else
                        {
                            indexOf[t.Thing] = takes.Count;
                            takes.Add(new TripTake
                            {
                                Thing = t.Thing,
                                Position = t.Position,
                                Count = t.Count,
                                UnitMass = t.UnitMass,
                                CtVol = pn.Source.CtVol,
                            });
                        }
                    }
                }

                // Carry-tracker designation = argmax bypassed mass — exactly
                // what feasible() assumed (merging same-stack chunks only
                // increases the realizable bypass, never decreases it).
                // First-wins on ties for determinism.
                int ct = -1;
                float bestByp = -1f;
                for (int i = 0; i < takes.Count; i++)
                {
                    if (takes[i].CtVol <= 0)
                        continue;
                    float byp = Math.Min(takes[i].Count, takes[i].CtVol) * takes[i].UnitMass;
                    if (byp > bestByp)
                    {
                        bestByp = byp;
                        ct = i;
                    }
                }

                var pickups = new List<HaulPickup>(takes.Count + 1);
                var positions = new List<IntVec3>(takes.Count + 1);
                for (int i = 0; i < takes.Count; i++)
                {
                    TripTake t = takes[i];
                    if (i == ct)
                    {
                        int ctTake = Math.Min(t.Count, t.CtVol);
                        pickups.Add(new HaulPickup
                        {
                            Thing = t.Thing,
                            Count = ctTake,
                            Destination = PickupDestination.CarryTracker,
                        });
                        positions.Add(t.Position);
                        // Volume overflow rides as Inventory in the same trip
                        // (Sweep's split rule).
                        if (t.Count > ctTake)
                        {
                            pickups.Add(new HaulPickup
                            {
                                Thing = t.Thing,
                                Count = t.Count - ctTake,
                                Destination = PickupDestination.Inventory,
                            });
                            positions.Add(t.Position);
                        }
                    }
                    else
                    {
                        pickups.Add(new HaulPickup
                        {
                            Thing = t.Thing,
                            Count = t.Count,
                            Destination = PickupDestination.Inventory,
                        });
                        positions.Add(t.Position);
                    }
                }

                int[] order = HeldKarp.Order(positions, wb);
                var ordered = new List<HaulPickup>(pickups.Count);
                for (int s = 0; s < order.Length; s++)
                    ordered.Add(pickups[order[s]]);
                trips.Add(new HaulTrip { Pickups = ordered });
            }

            return new HaulPlan
            {
                Trips = trips,
                ExecutionStrategy = HaulPlanExecutionStrategy.UwuCarryInventoryHybrid,
            };
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        private static HaulPlan EmptyPlan()
        {
            return new HaulPlan
            {
                Trips = new List<HaulTrip>(),
                ExecutionStrategy = HaulPlanExecutionStrategy.UwuCarryInventoryHybrid,
            };
        }

        private static HaulPlan GuardTripped(string reason)
        {
            if (EmitGuardLogs)
            {
                Log.Message("[Unique Weapons Unbound] Thorough haul planner guard "
                    + "tripped (" + reason + "); deferring to the next planner "
                    + "in the ladder.");
            }
            return null;
        }

        private static int IndexOfLowBit(int bit)
        {
            int i = 0;
            while (bit > 1)
            {
                bit >>= 1;
                i++;
            }
            return i;
        }

        private static long Pow3(int n)
        {
            long r = 1;
            for (int i = 0; i < n; i++)
                r *= 3;
            return r;
        }
    }
}

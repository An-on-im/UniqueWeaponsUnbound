using System;
using System.Collections.Generic;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning.Internal
{
    // Held-Karp dynamic programming over Manhattan distances (Held & Karp
    // 1962). Two entry points: the single-trip solver both planners use to cost
    // and sequence a trip's pickups, and SubsetTourTable, the all-subsets table
    // the Thorough planner queries during its partition DP. Costs stay int
    // throughout — Manhattan distance is integral, and float ties would
    // threaten determinism.
    //
    // Both entry points dedupe by position: callers may pass the same cell
    // several times (a carry-tracker/inventory split of one stack, co-located
    // stacks of different defs) and same-cell entries are solved as a single
    // TSP node, then kept adjacent in first-occurrence order on output.
    internal static class HeldKarp
    {
        // Cap on unique positions per exact solve — keeps the dp[2^k * k] table
        // bounded. Above the cap the solver degrades to first-occurrence order
        // (deterministic, never wrong, just not optimal). Planners are expected
        // to guard well below this.
        internal const int MaxUniquePositions = 16;

        internal const int Unreached = int.MaxValue;

        // Cost of the cheapest closed tour: depot → every unique position in
        // positions → depot. Duplicate cells cost nothing extra (the pawn is
        // already standing there).
        internal static int Cost(List<IntVec3> positions, IntVec3 depot)
        {
            List<IntVec3> unique = Dedupe(positions, null);
            int m = unique.Count;
            if (m == 0) return 0;
            if (m == 1) return 2 * HaulMath.ManhattanDist(unique[0], depot);
            if (m == 2)
            {
                return HaulMath.ManhattanDist(depot, unique[0])
                    + HaulMath.ManhattanDist(unique[0], unique[1])
                    + HaulMath.ManhattanDist(unique[1], depot);
            }
            if (m > MaxUniquePositions)
                return PathCost(unique, depot);

            int size = 1 << m;
            int[] dp = new int[size * m];
            int[] dWB = new int[m];
            RunDp(unique, depot, dp, null, dWB);

            int full = size - 1;
            int best = Unreached;
            for (int i = 0; i < m; i++)
            {
                int total = dp[full * m + i];
                if (total == Unreached) continue;
                total += dWB[i];
                if (total < best) best = total;
            }
            return best;
        }

        // Visit order for a trip's pickups, as indices into positions: unique
        // positions sequenced exactly, entries sharing a cell kept adjacent in
        // input order. Trivial sizes (≤ 2 unique cells, where every order costs
        // the same) and above-cap inputs return the first-occurrence grouping
        // unchanged.
        internal static int[] Order(List<IntVec3> positions, IntVec3 depot)
        {
            int n = positions.Count;
            var uniqueIndexOf = new int[n];
            List<IntVec3> unique = Dedupe(positions, uniqueIndexOf);
            int m = unique.Count;

            int[] uniqueOrder;
            if (m <= 2 || m > MaxUniquePositions)
            {
                uniqueOrder = new int[m];
                for (int u = 0; u < m; u++) uniqueOrder[u] = u;
            }
            else
            {
                uniqueOrder = SolveUniqueOrder(unique, depot);
            }

            var order = new int[n];
            int next = 0;
            for (int u = 0; u < m; u++)
            {
                int cell = uniqueOrder[u];
                for (int i = 0; i < n; i++)
                {
                    if (uniqueIndexOf[i] == cell) order[next++] = i;
                }
            }
            return order;
        }

        // Core DP shared by the single-trip solver and the subset table:
        // dp[mask * m + i] = cheapest path that leaves the depot, visits
        // exactly the positions in mask, and ends at position i. Forward
        // relaxation with strict less-than, so ties resolve to the lowest
        // (mask, i) found — deterministic. parent may be null when only costs
        // are needed.
        internal static void RunDp(
            List<IntVec3> pts, IntVec3 depot, int[] dp, int[] parent, int[] dWB)
        {
            int m = pts.Count;
            int[,] d = new int[m, m];
            for (int i = 0; i < m; i++)
            {
                dWB[i] = HaulMath.ManhattanDist(pts[i], depot);
                for (int j = 0; j < m; j++)
                    d[i, j] = HaulMath.ManhattanDist(pts[i], pts[j]);
            }

            int size = 1 << m;
            for (int idx = 0; idx < size * m; idx++)
            {
                dp[idx] = Unreached;
                if (parent != null) parent[idx] = -1;
            }
            for (int i = 0; i < m; i++)
                dp[(1 << i) * m + i] = dWB[i];

            for (int mask = 1; mask < size; mask++)
            {
                int baseIdx = mask * m;
                for (int i = 0; i < m; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    int cur = dp[baseIdx + i];
                    if (cur == Unreached) continue;
                    for (int j = 0; j < m; j++)
                    {
                        if ((mask & (1 << j)) != 0) continue;
                        int candidate = cur + d[i, j];
                        int target = (mask | (1 << j)) * m + j;
                        if (candidate < dp[target])
                        {
                            dp[target] = candidate;
                            if (parent != null) parent[target] = i;
                        }
                    }
                }
            }
        }

        private static int[] SolveUniqueOrder(List<IntVec3> unique, IntVec3 depot)
        {
            int m = unique.Count;
            int size = 1 << m;
            int[] dp = new int[size * m];
            int[] parent = new int[size * m];
            int[] dWB = new int[m];
            RunDp(unique, depot, dp, parent, dWB);

            int full = size - 1;
            int bestEnd = -1;
            int bestCost = Unreached;
            for (int i = 0; i < m; i++)
            {
                int total = dp[full * m + i];
                if (total == Unreached) continue;
                total += dWB[i];
                if (total < bestCost) { bestCost = total; bestEnd = i; }
            }

            var order = new int[m];
            int curIdx = bestEnd;
            int curMask = full;
            for (int s = m - 1; s >= 0; s--)
            {
                order[s] = curIdx;
                int prev = parent[curMask * m + curIdx];
                curMask ^= 1 << curIdx;
                if (prev < 0) break;
                curIdx = prev;
            }
            return order;
        }

        // Closed-tour cost visiting positions in the given order — the
        // deterministic fallback above the unique-position cap.
        private static int PathCost(List<IntVec3> unique, IntVec3 depot)
        {
            int total = HaulMath.ManhattanDist(depot, unique[0]);
            for (int i = 1; i < unique.Count; i++)
                total += HaulMath.ManhattanDist(unique[i - 1], unique[i]);
            total += HaulMath.ManhattanDist(unique[unique.Count - 1], depot);
            return total;
        }

        // Unique positions in first-occurrence order. When indexOf is
        // non-null, fills it with each input's index into the returned list.
        private static List<IntVec3> Dedupe(List<IntVec3> positions, int[] indexOf)
        {
            var unique = new List<IntVec3>();
            var seen = new Dictionary<IntVec3, int>();
            for (int i = 0; i < positions.Count; i++)
            {
                if (!seen.TryGetValue(positions[i], out int at))
                {
                    at = unique.Count;
                    seen[positions[i]] = at;
                    unique.Add(positions[i]);
                }
                if (indexOf != null) indexOf[i] = at;
            }
            return unique;
        }
    }

    // One Held-Karp pass over a set of unique positions, queryable for the
    // closed-tour cost of ANY position subset: tour(mask) = min over ends i in
    // mask of dp[mask][i] + dist(i, depot). Built in O(2^M * M^2); each query
    // is an array read. This is what lets the Thorough planner's partition DP
    // evaluate every candidate trip without per-subset solves.
    internal sealed class SubsetTourTable
    {
        private readonly int[] tourCost;

        internal SubsetTourTable(List<IntVec3> uniquePositions, IntVec3 depot)
        {
            int m = uniquePositions.Count;
            if (m > HeldKarp.MaxUniquePositions)
            {
                throw new ArgumentException(
                    "SubsetTourTable built over " + m + " positions; cap is "
                    + HeldKarp.MaxUniquePositions + " (caller must guard).");
            }

            int size = 1 << m;
            tourCost = new int[size];
            if (m == 0) return;

            int[] dp = new int[size * m];
            int[] dWB = new int[m];
            HeldKarp.RunDp(uniquePositions, depot, dp, null, dWB);

            for (int mask = 1; mask < size; mask++)
            {
                int best = HeldKarp.Unreached;
                for (int i = 0; i < m; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    int c = dp[mask * m + i];
                    if (c == HeldKarp.Unreached) continue;
                    c += dWB[i];
                    if (c < best) best = c;
                }
                tourCost[mask] = best;
            }
        }

        internal int TourCost(int posMask)
        {
            return tourCost[posMask];
        }
    }
}

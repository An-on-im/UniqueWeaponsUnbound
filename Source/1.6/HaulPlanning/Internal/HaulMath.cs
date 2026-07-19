using System;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound.HaulPlanning.Internal
{
    // Capacity and distance primitives shared by the haul planners. Pulled out
    // of SweepHaulPlanner so Sweep and Thorough agree exactly on the capacity
    // model and geometry — a constant that drifted between them would make the
    // two planners disagree about feasibility of the same trip, breaking the
    // degradation ladder's "lower rung is laxer" property.
    internal static class HaulMath
    {
        // 5% headroom on per-trip inventory mass — matches PUAH's stop-just-
        // before-the-encumbrance-threshold pattern and absorbs float rounding.
        internal const float CapacityFactor = 0.95f;

        internal const float MassEpsilon = 1e-3f;

        // Manhattan distance on the map grid — matches RimWorld's grid pathing
        // better than Euclidean, preserves triangle inequality, and stays
        // integral (float tour-cost ties would threaten determinism).
        internal static int ManhattanDist(IntVec3 a, IntVec3 b)
        {
            return Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z);
        }

        // Replicates Pawn_CarryTracker.MaxStackSpaceEver: the minimum of the
        // def's stack limit and how many units fit under the pawn's carrying
        // capacity by volume. The carry tracker doesn't enforce mass — only
        // volume — so a pawn can over-carry on mass via this slot.
        internal static int MaxStackSpaceEver(ThingDef def, float capacityKg)
        {
            if (def.VolumePerUnit <= 0f) return def.stackLimit;
            int volBound = Mathf.RoundToInt(capacityKg / def.VolumePerUnit);
            return Mathf.Min(def.stackLimit, volBound);
        }
    }
}

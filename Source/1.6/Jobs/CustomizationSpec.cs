using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    public enum OpType
    {
        RemoveTrait,
        AddTrait,
        ApplyCosmetics,
    }

    public class CustomizationOp : IExposable
    {
        public OpType type;
        public WeaponTraitDef trait;
        public List<ThingDefCountClass> cost;
        public List<ThingDefCountClass> refund;

        // Optional color change on this specific op (null = no color change at this step)
        public ColorDef colorToApply;

        // If true, clear the color to default (no tint). Used when removing a
        // forced-color trait with no remaining forced-color traits active.
        public bool clearColor;

        // Only for ApplyCosmetics ops
        public string nameToApply;
        public int? textureIndexToApply;

        public void ExposeData()
        {
            Scribe_Values.Look(ref type, "type");
            Scribe_Defs.Look(ref trait, "trait");
            Scribe_Collections.Look(ref cost, "cost", LookMode.Deep);
            Scribe_Collections.Look(ref refund, "refund", LookMode.Deep);
            Scribe_Defs.Look(ref colorToApply, "colorToApply");
            Scribe_Values.Look(ref clearColor, "clearColor", false);
            Scribe_Values.Look(ref nameToApply, "nameToApply", null);
            Scribe_Values.Look(ref textureIndexToApply, "textureIndexToApply", null);
        }
    }

    // Data transfer object between Dialog_WeaponCustomization and
    // JobDriver_CustomizeWeapon. The dialog writes this directly to the
    // driver's spec field on confirm via JobDriver_CustomizeWeapon.SetSpec, so
    // the (scribed) field carries it across save/reload taken in the gap
    // between the dialog's Close() and the consumeSpec toil.
    public class CustomizationSpec : IExposable
    {
        // Ordered operations: removals → cosmetics → additions. Each op carries
        // its own per-op cost and optional cosmetic changes.
        public List<CustomizationOp> operations;

        // The final ThingDef the weapon should have after all operations. Used
        // for def conversion decisions (base↔unique).
        public ThingDef resultingDef;

        // Aggregate net resource cost across all operations (addition costs
        // minus expected refunds). Used for pre-flight ingredient reservation
        // and hauling.
        public List<ThingDefCountClass> totalCost;

        // Aggregate resource refund from all removal operations (raw costs
        // aggregated then floored once per material). Initializes the job
        // driver's virtual refund ledger, which offsets addition costs and
        // spawns any surplus at job end.
        public List<ThingDefCountClass> totalRefund;

        // The final color to apply after all operations complete. Set from
        // EffectiveColor in the dialog. Applied in the finalize toil to ensure
        // it persists through Setup() calls and def conversions. Null means no
        // color change (e.g., reverting to base with no unique comp).
        public ColorDef finalColor;

        // The desired texture variant index. Applied during base→unique
        // conversion so the weapon immediately shows the correct texture rather
        // than a stale or random one until the ApplyCosmetics op runs later in
        // the work loop.
        public int? finalTextureIndex;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref operations, "operations", LookMode.Deep);
            Scribe_Defs.Look(ref resultingDef, "resultingDef");
            Scribe_Collections.Look(ref totalCost, "totalCost", LookMode.Deep);
            Scribe_Collections.Look(ref totalRefund, "totalRefund", LookMode.Deep);
            Scribe_Defs.Look(ref finalColor, "finalColor");
            Scribe_Values.Look(ref finalTextureIndex, "finalTextureIndex", null);
        }
    }
}

using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace UniqueWeaponsUnbound
{
    // Work phase: ledger reads/writes against placedIngredients (populated
    // by the Haul phase) and the refundLedger float credits, then per-op
    // mutation of the weapon Thing. Pays each op's cost from the ledger
    // before applying the trait/cosmetics/color change, and converts
    // base<->unique atomically when the trait count crosses the 0<->1
    // boundary. A throw inside an op bails the whole job rather than
    // continuing onto the next op (which could blow trait limits or
    // depend on a refund credit that never materialised).
    public partial class JobDriver_CustomizeWeapon
    {
        // Consumes resources from the tracked placedIngredients list rather
        // than scanning nearby cells. Mirrors vanilla's pattern of consuming
        // from job.placedThings. Destroyed stacks are removed from the list.
        private bool ConsumeFromPlacedIngredients(List<ThingDefCountClass> costs)
        {
            foreach (ThingDefCountClass cost in costs)
            {
                int remaining = cost.count;
                for (int i = placedIngredients.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    Thing stack = placedIngredients[i];
                    if (stack.Destroyed || !stack.Spawned || stack.def != cost.thingDef)
                        continue;

                    int take = Mathf.Min(remaining, stack.stackCount);
                    remaining -= take;

                    if (take >= stack.stackCount)
                    {
                        stack.Destroy();
                        placedIngredients.RemoveAt(i);
                    }
                    else
                    {
                        stack.SplitOff(take).Destroy();
                    }
                }

                if (remaining > 0)
                {
                    Log.Warning($"[Unique Weapons Unbound] Could not consume all " +
                        $"{cost.thingDef.LabelCap} from placed ingredients: " +
                        $"needed {cost.count}, short by {remaining}.");
                    return false;
                }
            }
            return true;
        }

        // Returns the total reservable count of thingDef across all currently
        // placed ingredient stacks, ignoring destroyed/despawned ones.
        private int CountInPlaced(ThingDef thingDef)
        {
            int available = 0;
            for (int i = 0; i < placedIngredients.Count; i++)
            {
                Thing stack = placedIngredients[i];
                if (stack.Destroyed || !stack.Spawned || stack.def != thingDef)
                    continue;
                available += stack.stackCount;
            }
            return available;
        }

        // Returns true if an op's cost could currently be paid from the refund
        // ledger plus placed ingredients, without committing any state. Used as
        // a pre-flight check before starting an op's work cycle so the pawn
        // doesn't waste 1000 ticks of work on an op we already know will abort.
        private bool CanAffordOpCost(List<ThingDefCountClass> opCost)
        {
            if (opCost == null || opCost.Count == 0)
                return true;

            foreach (ThingDefCountClass cost in opCost)
            {
                int remaining = cost.count;
                if (refundLedger.TryGetValue(cost.thingDef, out float credit) && credit > 0f)
                    remaining -= Mathf.Min(remaining, Mathf.FloorToInt(credit));
                if (remaining > 0 && CountInPlaced(cost.thingDef) < remaining)
                    return false;
            }
            return true;
        }

        // Pays an op's cost: debits the refund ledger first, then consumes the
        // remainder from placed ingredient stacks at the workbench. Pre-checks
        // availability and only commits if the cost can be fully paid, so a
        // shortfall (e.g. ingredients destroyed by
        // fire/explosion/deterioration) leaves the ledger and the weapon
        // untouched. Returns false on shortfall — caller should notify the
        // player and abort the job.
        private bool TryConsumeOpCost(List<ThingDefCountClass> opCost)
        {
            if (opCost == null || opCost.Count == 0)
                return true;

            // First pass: compute what we'd take from the ledger and from placed
            // ingredients, without committing.
            var fromPlaced = new List<ThingDefCountClass>();
            var pendingDebit = new Dictionary<ThingDef, int>();
            foreach (ThingDefCountClass cost in opCost)
            {
                int remaining = cost.count;
                if (refundLedger.TryGetValue(cost.thingDef, out float credit) && credit > 0f)
                {
                    int debit = Mathf.Min(remaining, Mathf.FloorToInt(credit));
                    if (debit > 0)
                    {
                        pendingDebit[cost.thingDef] = debit;
                        remaining -= debit;
                    }
                }
                if (remaining > 0)
                    fromPlaced.Add(new ThingDefCountClass(cost.thingDef, remaining));
            }

            // Verify the placed-ingredient remainder can be satisfied before
            // mutating any state.
            foreach (ThingDefCountClass need in fromPlaced)
            {
                if (CountInPlaced(need.thingDef) < need.count)
                    return false;
            }

            // Commit ledger debits and ingredient consumption.
            foreach (KeyValuePair<ThingDef, int> kv in pendingDebit)
                refundLedger[kv.Key] -= kv.Value;
            if (fromPlaced.Count > 0)
                ConsumeFromPlacedIngredients(fromPlaced);
            return true;
        }

        private void ApplyOperation(CustomizationOp op)
        {
            try
            {
                ApplyOperationInner(op);
            }
            catch (Exception ex)
            {
                // The weapon may be in a partial state — e.g. cost paid but
                // trait not yet added, or trait added but ability comp not
                // wired. Continuing would compound the damage: a failed remove
                // leaves the trait in place (and no refund credit to the
                // ledger), so a subsequent add could push the count past the
                // trait limit and/or run short on materials the refund was
                // funding. Bail here; placed ingredients consumed by
                // TryConsumeOpCost prior to the throw are not recovered.
                RecordOpFailureBail(op, ex);
                EndJobWith(JobCondition.Incompletable);
            }
        }

        // Records a structured log line plus a translated, op-type-specific
        // bail message for an unexpected throw inside ApplyOperation. The log
        // names the op index, op type, trait defName, and weapon defName so
        // post-mortem triage doesn't have to reconstruct the failing op from
        // the surrounding toil context. The bail message is routed through the
        // first-set-wins SetBailMessage channel so a cascade failure can't
        // overwrite the original cause.
        private void RecordOpFailureBail(CustomizationOp op, Exception ex)
        {
            string opDescr;
            string bailMessageText;
            switch (op.type)
            {
                case OpType.AddTrait:
                    opDescr = "adding trait " + (op.trait?.defName ?? "(null)");
                    bailMessageText = "UWU_BailOpAddTraitFailed".Translate(
                        WeaponLabel, op.trait?.LabelCap ?? "");
                    break;
                case OpType.RemoveTrait:
                    opDescr = "removing trait " + (op.trait?.defName ?? "(null)");
                    bailMessageText = "UWU_BailOpRemoveTraitFailed".Translate(
                        WeaponLabel, op.trait?.LabelCap ?? "");
                    break;
                case OpType.ApplyCosmetics:
                    opDescr = "applying cosmetics";
                    bailMessageText = "UWU_BailOpCosmeticsFailed".Translate(WeaponLabel);
                    break;
                default:
                    opDescr = "op type " + op.type;
                    bailMessageText = "UWU_BailUnexpected".Translate(WeaponLabel);
                    break;
            }

            int totalOps = spec?.operations?.Count ?? -1;
            string weaponDefName = weapon?.def?.defName
                ?? job?.GetTarget(WeaponIndex).Thing?.def?.defName
                ?? "(null)";
            Log.Error("[Unique Weapons Unbound] Customization aborted while " + opDescr
                + " on " + WeaponLabel + " [" + weaponDefName + "] "
                + "(op " + (currentOpIndex + 1) + "/" + totalOps + "): " + ex);
            SetBailMessage(bailMessageText);
        }

        private void ApplyOperationInner(CustomizationOp op)
        {
            switch (op.type)
            {
                case OpType.RemoveTrait:
                    // Negative-trait removals carry a cost (op.cost). Pay it before
                    // removing the trait so a placed-ingredient shortfall can't leave
                    // the trait already gone with no payment recorded.
                    if (!TryConsumeOpCost(op.cost))
                    {
                        RecordShortfallBail(op.trait);
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    WeaponModificationUtility.RemoveTrait(weapon, op.trait);

                    // Credit refund to the virtual ledger atomically with the removal.
                    // Raw costs are stored on the op; apply CostMultiplier and RefundRate
                    // here as float to defer rounding until resources are actually spawned
                    // or consumed.
                    if (op.refund != null)
                    {
                        foreach (ThingDefCountClass refund in op.refund)
                        {
                            float credit = refund.count
                                * TraitCostUtility.CostMultiplier
                                * TraitCostUtility.RefundRate;
                            if (refundLedger.ContainsKey(refund.thingDef))
                                refundLedger[refund.thingDef] += credit;
                            else
                                refundLedger[refund.thingDef] = credit;
                        }
                    }

                    // If removing the last trait, convert unique→base atomically
                    CompUniqueWeapon removeComp = weapon.TryGetComp<CompUniqueWeapon>();
                    if (removeComp != null && removeComp.TraitsListForReading.Count == 0
                        && UWU_Mod.Settings.allowDefConversion)
                    {
                        ThingDef baseDef = WeaponRegistry.GetBaseVariant(weapon.def);
                        if (baseDef != null)
                            ConvertWeaponInPlace(baseDef);
                    }
                    break;

                case OpType.ApplyCosmetics:
                    if (weapon.TryGetComp<CompUniqueWeapon>() != null)
                    {
                        if (op.nameToApply != null)
                            WeaponModificationUtility.SetName(weapon, op.nameToApply);
                        if (op.textureIndexToApply.HasValue)
                            WeaponModificationUtility.SetTextureIndex(weapon, op.textureIndexToApply.Value);
                    }
                    break;

                case OpType.AddTrait:
                    // Pay the cost first — if placed ingredients have been destroyed
                    // (fire, explosion, deterioration), abort cleanly before any
                    // mutation (def conversion, trait add) leaves a partial state.
                    if (!TryConsumeOpCost(op.cost))
                    {
                        RecordShortfallBail(op.trait);
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    // If weapon is currently base, convert base→unique first
                    if (!WeaponRegistry.IsUniqueWeapon(weapon.def) && UWU_Mod.Settings.allowDefConversion)
                    {
                        ThingDef uniqueDef = WeaponRegistry.GetUniqueVariant(weapon.def);
                        if (uniqueDef != null)
                            ConvertWeaponInPlace(uniqueDef);
                    }

                    WeaponModificationUtility.AddTrait(weapon, op.trait);

                    // Apply bundled cosmetics (merged from a cosmetics op that
                    // would have been a no-op when the weapon was in base state)
                    if (weapon.TryGetComp<CompUniqueWeapon>() != null)
                    {
                        if (op.nameToApply != null)
                            WeaponModificationUtility.SetName(weapon, op.nameToApply);
                        if (op.textureIndexToApply.HasValue)
                            WeaponModificationUtility.SetTextureIndex(weapon, op.textureIndexToApply.Value);
                    }
                    break;
            }

            // Apply color change if this op carries one
            if (weapon.TryGetComp<CompUniqueWeapon>() != null)
            {
                if (op.clearColor)
                    WeaponModificationUtility.SetColor(weapon, null);
                else if (op.colorToApply != null)
                    WeaponModificationUtility.SetColor(weapon, op.colorToApply);
            }
        }

        // Converts the weapon to a different ThingDef in-place (base↔unique).
        // Destroys the current weapon, spawns a new one at the same position,
        // and updates reservations. Called atomically within an ApplyOperation
        // step when a trait change crosses the 0↔1 boundary.
        private void ConvertWeaponInPlace(ThingDef targetDef)
        {
            Thing newWeapon = WeaponDefConversion.ConvertWeaponDef(weapon, targetDef);
            IntVec3 pos = weapon.Position;
            Map map = weapon.Map;

            // Transfer relic status and authored art BEFORE destroying the old
            // weapon: relic transfer keeps Thing.Destroy() from firing
            // Notify_ThingLost on the precept, and art transfer hands off the
            // TaleReference before PostDestroy would tear it down.
            WeaponDefConversion.TransferRelicStatus(weapon, newWeapon);
            WeaponDefConversion.TransferArt(weapon, newWeapon);

            if (weapon.Spawned)
                weapon.Destroy();
            else if (!weapon.Destroyed)
                weapon.Destroy();

            // Apply desired texture on base→unique so it doesn't flash a random variant
            if (WeaponRegistry.IsUniqueWeapon(targetDef)
                && spec.finalTextureIndex.HasValue)
                WeaponModificationUtility.SetTextureIndex(newWeapon, spec.finalTextureIndex.Value);

            GenSpawn.Spawn(newWeapon, pos, map);
            pawn.Reserve(newWeapon, job);
            pawn.Map.physicalInteractionReservationManager.Reserve(pawn, job, newWeapon);
            weapon = newWeapon;
            // Keep the job target in sync with the live weapon: a save taken
            // after a conversion must not scribe targetB as a destroyed ref.
            job.SetTarget(WeaponIndex, newWeapon);
        }
    }
}

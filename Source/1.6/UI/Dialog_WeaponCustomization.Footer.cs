using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UniqueWeaponsUnbound.HaulPlanning;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    public partial class Dialog_WeaponCustomization
    {
        // --- Shared drawing helpers ---

        private void DrawCostIcons(
            Rect rect, List<ThingDefCountClass> costs, bool rightAlign = false,
            HashSet<ThingDef> insufficientResources = null, bool greenQuantities = false,
            int maxVisible = 0)
        {
            // TODO: Decide behavior for uncraftable weapons — TraitCostUtility returns
            // empty list. Currently shows nothing. May want "Free" label or a warning.
            if (costs == null || costs.Count == 0)
                return;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;

            // When the list exceeds maxVisible, show (maxVisible - 1) items + ellipsis
            // to signal that more is hidden and prompt the player to hover for the tooltip.
            bool truncated = maxVisible > 0 && costs.Count > maxVisible;
            int shownCount = truncated ? maxVisible - 1 : costs.Count;
            const string ellipsis = "...";

            float curX;
            if (rightAlign)
            {
                // Pre-calculate total width so we can start from the right edge
                float totalWidth = 0f;
                for (int i = 0; i < shownCount; i++)
                {
                    totalWidth += CostIconSize + 1f;
                    totalWidth += Text.CalcSize("x" + costs[i].count).x + 6f;
                }
                if (truncated)
                    totalWidth += Text.CalcSize(ellipsis).x + 6f;
                if (totalWidth > 0f)
                    totalWidth -= 6f; // Remove trailing gap
                curX = Mathf.Max(rect.x, rect.xMax - totalWidth);
            }
            else
            {
                curX = rect.x;
            }

            for (int i = 0; i < shownCount; i++)
            {
                ThingDefCountClass cost = costs[i];
                if (curX + CostIconSize > rect.xMax)
                    break;

                // Material icon
                Rect iconRect = new Rect(curX, rect.y + (rect.height - CostIconSize) / 2f,
                    CostIconSize, CostIconSize);
                Widgets.ThingIcon(iconRect, cost.thingDef);
                curX += CostIconSize + 1f;

                // Count label — red when insufficient, green for refunds
                string countText = "x" + cost.count;
                float textWidth = Text.CalcSize(countText).x;
                Rect textRect = new Rect(curX, rect.y, textWidth, rect.height);
                bool isShort = insufficientResources != null
                    && insufficientResources.Contains(cost.thingDef);
                if (isShort)
                {
                    Color prevCostColor = GUI.color;
                    GUI.color = new Color(0.9f, 0.2f, 0.2f);
                    Widgets.Label(textRect, countText);
                    GUI.color = prevCostColor;
                }
                else if (greenQuantities)
                {
                    Color prevCostColor = GUI.color;
                    GUI.color = new Color(0.4f, 0.8f, 0.4f);
                    Widgets.Label(textRect, countText);
                    GUI.color = prevCostColor;
                }
                else
                {
                    Widgets.Label(textRect, countText);
                }
                curX += textWidth + 6f;
            }

            if (truncated)
            {
                float ellipsisWidth = Text.CalcSize(ellipsis).x;
                Rect ellipsisRect = new Rect(curX, rect.y, ellipsisWidth, rect.height);
                Widgets.Label(ellipsisRect, ellipsis);
            }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // --- Footer ---

        private void DrawFooter(Rect inRect)
        {
            float footerY = inRect.yMax - FooterHeight;
            float buttonY = footerY + (FooterHeight - ButtonSize.y) / 2f;

            // Cancel button (left-aligned)
            Rect cancelRect = new Rect(
                inRect.x,
                buttonY,
                ButtonSize.x,
                ButtonSize.y);
            if (Widgets.ButtonText(cancelRect, "UWU_Cancel".Translate()))
            {
                Close();
            }

            // Reset button (center-aligned)
            Rect resetRect = new Rect(
                inRect.x + (inRect.width - ButtonSize.x) / 2f,
                buttonY,
                ButtonSize.x,
                ButtonSize.y);
            if (HasChanges)
            {
                if (Widgets.ButtonText(resetRect, "UWU_Reset".Translate()))
                {
                    ResetToOriginal();
                }
            }
            else
            {
                Color prevColor = GUI.color;
                GUI.color = Color.gray;
                Widgets.ButtonText(resetRect, "UWU_Reset".Translate());
                GUI.color = prevColor;
            }

            // Confirm button (right-aligned)
            Rect confirmRect = new Rect(
                inRect.xMax - ButtonSize.x,
                buttonY,
                ButtonSize.x,
                ButtonSize.y);

            // Determine if confirm should be enabled
            bool canConfirm = HasChanges;
            bool insufficientForConfirm = false;
            if (canConfirm)
            {
                if (insufficientResources != null && insufficientResources.Count > 0)
                {
                    canConfirm = false;
                    insufficientForConfirm = true;
                }
            }

            if (canConfirm)
            {
                if (Widgets.ButtonText(confirmRect, "UWU_Confirm".Translate()))
                {
                    // Auto-generate name if result is unique but name is empty
                    if (ResultingDef == uniqueDef
                        && string.IsNullOrEmpty(desiredName)
                        && desiredTraits.Count > 0)
                    {
                        string regenerated = GenerateWeaponName();
                        if (regenerated != null)
                            desiredName = regenerated;
                    }

                    // Build ordered operations list and spec — use net cost
                    // (total addition cost minus expected refunds) for hauling.
                    var spec = BuildCustomizationSpec(currentNetCost);

                    // Reserve ingredients synchronously while forcePause holds the game
                    // still — no other pawn AI runs between the per-frame availability
                    // check and this commit, so what the player saw is what they get.
                    //
                    // Explicit CurJob null guard: in a single-player session forcePause
                    // + absorbInputAroundWindow makes this impossible, but RimWorld
                    // Multiplayer doesn't enforce pause across clients — a peer could
                    // retarget our pawn between dialog open and confirm. The downstream
                    // driver lookup inside TryReserveIngredientsForJob would also catch
                    // this, but a paired check at the call site keeps the multiplayer-
                    // readiness visible here next to the reservation call it protects.
                    if (pawn.CurJob == null)
                    {
                        HandleReservationFailure(
                            IngredientReservation.ReservationResult.NoActiveDriver());
                        return;
                    }
                    var result = IngredientReservation.TryReserveIngredientsForJob(
                        pawn, spec.totalCost);
                    if (!result.IsSuccess)
                    {
                        // Leave the dialog open (per-frame availability recompute
                        // reveals new state on the next render) and surface both a
                        // log line and a player-visible message via the helper, so
                        // log + Messages text agree and the click isn't perceived
                        // as a no-op. NoActiveDriver is a genuine invariant break
                        // and logs at Error; the others at Warning.
                        HandleReservationFailure(result);
                        return;
                    }

                    // Set the spec directly on the driver field (not the static
                    // pending-spec dict) so the field-scribe carries it across a
                    // save/reload taken in the one-tick gap between Close() and
                    // the consumeSpec toil — autosave landing in that gap would
                    // otherwise orphan a confirmed customization.
                    ((JobDriver_CustomizeWeapon)pawn.jobs.curDriver).SetSpec(spec);
                    Close();
                }
            }
            else
            {
                Color prevColor = GUI.color;
                GUI.color = Color.gray;
                Widgets.ButtonText(confirmRect, "UWU_Confirm".Translate());
                GUI.color = prevColor;

                if (insufficientForConfirm)
                {
                    // Red error text immediately left of the confirm button
                    string errorText = "UWU_MissingResources".Translate();
                    float errorWidth = Text.CalcSize(errorText).x;
                    Rect errorRect = new Rect(
                        confirmRect.x - errorWidth - 8f,
                        confirmRect.y,
                        errorWidth,
                        confirmRect.height);
                    Color prevColor2 = GUI.color;
                    GUI.color = new Color(0.9f, 0.2f, 0.2f);
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(errorRect, errorText);
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = prevColor2;

                    TaggedString tooltip = "UWU_NotEnoughMaterials".Translate();
                    TooltipHandler.TipRegion(errorRect, tooltip);
                    TooltipHandler.TipRegion(confirmRect, tooltip);
                }
            }
        }

        // --- Spec Building ---

        private CustomizationSpec BuildCustomizationSpec(List<ThingDefCountClass> totalCost)
        {
            var ops = new List<CustomizationOp>();
            List<WeaponTraitDef> removes = TraitsToRemove.ToList();
            List<WeaponTraitDef> adds = TraitsToAdd.ToList();

            bool hasForcedColorTraits =
                removes.Any(t => t.forcedColor != null) ||
                adds.Any(t => t.forcedColor != null);

            // 1. Removal ops
            var remainingOriginalTraits = new List<WeaponTraitDef>(originalTraits);
            foreach (WeaponTraitDef trait in removes)
            {
                var op = new CustomizationOp
                {
                    type = OpType.RemoveTrait,
                    trait = trait,
                };

                if (TraitCostUtility.IsNegativeTrait(trait))
                {
                    // Negative trait removal costs resources (reduced by RefundRate)
                    op.cost = GetRemovalCost(trait);
                }
                else
                {
                    op.refund = UWU_Mod.Settings.traitRefundRate > 0f
                            && UWU_Mod.Settings.traitCostMultiplier > 0f
                        ? CachedPipelineCost(trait, isRemoval: false)
                        : null;
                }

                remainingOriginalTraits.Remove(trait);

                if (trait.forcedColor != null)
                {
                    // Revert to the next remaining forced color, or clear to default
                    // if no forced-color traits remain (weapon shows natural material).
                    ColorDef nextForced = null;
                    for (int i = remainingOriginalTraits.Count - 1; i >= 0; i--)
                    {
                        if (remainingOriginalTraits[i].forcedColor != null)
                        {
                            nextForced = remainingOriginalTraits[i].forcedColor;
                            break;
                        }
                    }
                    if (nextForced != null)
                        op.colorToApply = nextForced;
                    else
                        op.clearColor = true;
                }

                ops.Add(op);
            }

            // 2. Cosmetics op (only if result is unique)
            // If the weapon will be in base state after removals (all original
            // traits removed) and there are additions, cosmetics can't apply to
            // a base weapon. Merge them into the first AddTrait op instead,
            // which will convert base→unique and then apply cosmetics atomically.
            // Cosmetics can only apply to a unique weapon. We must defer them
            // onto the first AddTrait op if the weapon will be in base state when
            // the cosmetics step would run. Two cases:
            // (a) Weapon starts as unique but all original traits are removed → base
            // (b) Weapon starts as base (non-unique) → already in base state
            bool willBeBaseAfterRemovals = remainingOriginalTraits.Count == 0
                && WeaponRegistry.IsUniqueWeapon(weapon.def)
                && baseDef != null
                && UWU_Mod.Settings.allowDefConversion;
            bool startsAsBase = !WeaponRegistry.IsUniqueWeapon(weapon.def)
                && UWU_Mod.Settings.allowDefConversion;
            bool deferCosmetics = (willBeBaseAfterRemovals || startsAsBase) && adds.Count > 0;

            string deferredName = null;
            int? deferredTexture = null;
            ColorDef deferredColor = null;

            if (ResultingDef == uniqueDef)
            {
                bool nameChanged = desiredName != originalName;
                bool texChanged = desiredTextureIndex != originalTextureIndex;
                bool colorChanged = EffectiveColor != originalColor;

                if (deferCosmetics)
                {
                    // Always save ALL desired cosmetics when deferring. The round-trip
                    // through base state (unique→base→unique) destroys the CompUniqueWeapon,
                    // so existing name/color are lost even if unchanged by the player.
                    // They must be re-applied after the first AddTrait converts back to unique.
                    deferredName = desiredName;
                    deferredTexture = desiredTextureIndex;
                    if (!hasForcedColorTraits)
                        deferredColor = desiredColor;
                }
                else if (nameChanged || texChanged || (colorChanged && !hasForcedColorTraits))
                {
                    var cosOp = new CustomizationOp
                    {
                        type = OpType.ApplyCosmetics,
                    };

                    if (nameChanged)
                        cosOp.nameToApply = desiredName;
                    if (texChanged)
                        cosOp.textureIndexToApply = desiredTextureIndex;
                    if (colorChanged && !hasForcedColorTraits)
                        cosOp.colorToApply = desiredColor;

                    ops.Add(cosOp);
                }
            }

            // 3. Addition ops
            bool firstAdd = true;
            foreach (WeaponTraitDef trait in adds)
            {
                var op = new CustomizationOp
                {
                    type = OpType.AddTrait,
                    trait = trait,
                    cost = GetAdditionCost(trait),
                };

                if (trait.forcedColor != null)
                    op.colorToApply = trait.forcedColor;

                // Merge deferred cosmetics into the first AddTrait op
                if (firstAdd && deferCosmetics)
                {
                    op.nameToApply = deferredName;
                    op.textureIndexToApply = deferredTexture;
                    if (deferredColor != null && op.colorToApply == null)
                        op.colorToApply = deferredColor;
                    firstAdd = false;
                }

                ops.Add(op);
            }

            return new CustomizationSpec
            {
                operations = ops,
                resultingDef = ResultingDef,
                totalCost = totalCost,
                totalRefund = currentTotalRefund,
                finalColor = ResultingDef == uniqueDef ? EffectiveColor : null,
                finalTextureIndex = ResultingDef == uniqueDef ? desiredTextureIndex : (int?)null,
            };
        }

        public override void OnCancelKeyPressed()
        {
            Close();
        }

        // Maps a non-Success IngredientReservation.ReservationResult to a
        // paired log line + player-visible message describing what happened at
        // confirm time. Centralised so the two strings can't drift apart. For
        // IngredientReservation.ReservationOutcome.ReservationConflict the
        // result carries the specific def + count that failed to reserve, so
        // the message can name it concretely (e.g. "failed to reserve plasteel
        // x75") instead of saying "materials unavailable."
        private void HandleReservationFailure(IngredientReservation.ReservationResult result)
        {
            string logReason;
            string messageText;
            bool isInternalError = false;
            switch (result.Outcome)
            {
                case IngredientReservation.ReservationOutcome.NoActiveDriver:
                    logReason = "no active customize-weapon driver "
                        + "(invariant violation — dialog opened without our driver running)";
                    messageText = "UWU_CouldNotStartInternalError".Translate(weapon.LabelShortCap);
                    isInternalError = true;
                    break;
                case IngredientReservation.ReservationOutcome.PlanInfeasible:
                    logReason = "no haul planner could satisfy demand from the "
                        + "candidate pool (Sequential fallback also returned null)";
                    messageText = "UWU_CouldNotStartPlanInfeasible".Translate(weapon.LabelShortCap);
                    break;
                case IngredientReservation.ReservationOutcome.ReservationConflict:
                    string defLabel = result.ConflictDef?.label ?? "(unknown)";
                    string reserverInfo = result.ConflictReserver != null
                        ? " (held by " + result.ConflictReserver.LabelShortCap + ")"
                        : "";
                    logReason = "reservation conflict during commit "
                        + "(could not reserve " + defLabel + " x" + result.ConflictCount
                        + reserverInfo + ")";
                    // Pick the matching translation key — the held-by variant
                    // names the conflicting reserver so the player can find and
                    // investigate that pawn rather than retrying blindly.
                    messageText = result.ConflictReserver != null
                        ? "UWU_CouldNotStartReservationConflictHeldBy".Translate(
                            weapon.LabelShortCap, defLabel, result.ConflictCount,
                            result.ConflictReserver.LabelShortCap)
                        : "UWU_CouldNotStartReservationConflict".Translate(
                            weapon.LabelShortCap, defLabel, result.ConflictCount);
                    break;
                default:
                    return;
            }

            string logLine = "[Unique Weapons Unbound] Could not start customization of "
                + weapon.LabelShortCap + ": " + logReason + ". Dialog left open.";
            if (isInternalError)
                Log.Error(logLine);
            else
                Log.Warning(logLine);

            Messages.Message(
                messageText, weapon, MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}

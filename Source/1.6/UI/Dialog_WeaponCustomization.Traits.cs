using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    public partial class Dialog_WeaponCustomization
    {
        // --- Traits tab ---

        private void DrawTraitsTab(Rect rect)
        {
            // Empty compatible-trait pool: skip the search/filter chrome
            // entirely and let the centered "no compatible traits" message
            // own the full tab area, mirroring the disabled Texture/Color
            // tab pattern.
            if (compatibleTraits.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Color prevColor = GUI.color;
                GUI.color = Color.gray;
                Widgets.Label(rect, "UWU_NoTraitsAvailable".Translate());
                GUI.color = prevColor;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            float curY = rect.y + 6f;

            // Search field at top — matches vanilla z-search look and behaviour
            Rect searchRect = new Rect(rect.x, curY, rect.width - 6f, QuickSearchWidget.WidgetHeight);
            traitSearchWidget.OnGUI(searchRect);
            curY += QuickSearchWidget.WidgetHeight + 7f;

            // Reserve space at the bottom for the negative traits toggle
            float checkSize = 24f;
            float checkMargin = 5f;
            float checkAreaHeight = 4f + checkSize + checkMargin * 3f;
            float traitListHeight = rect.yMax - curY - checkAreaHeight;

            // Scrollable trait list
            Rect traitListOuterRect = new Rect(rect.x, curY, rect.width, traitListHeight);

            int visibleCount = 0;
            foreach (WeaponTraitDef trait in compatibleTraits)
            {
                if (ShouldShowTrait(trait))
                    visibleCount++;
            }
            traitSearchWidget.noResultsMatched = visibleCount == 0 && traitSearchWidget.filter.Active;

            if (visibleCount == 0)
            {
                // Empty list — explain which filter is responsible so the player can act on it.
                // Mirrors the centered-gray-label pattern used by the disabled texture/color tabs.
                string emptyMsg = GetEmptyTraitListMessage();
                if (!string.IsNullOrEmpty(emptyMsg))
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Color prevColor = GUI.color;
                    GUI.color = Color.gray;
                    Widgets.Label(traitListOuterRect, emptyMsg);
                    GUI.color = prevColor;
                    Text.Anchor = TextAnchor.UpperLeft;
                }
            }
            else
            {
                float totalTraitHeight = visibleCount * (TraitRowHeight + TraitRowGap);

                Rect traitListInnerRect = new Rect(0f, 0f,
                    traitListOuterRect.width - 16f, totalTraitHeight);

                Widgets.BeginScrollView(traitListOuterRect, ref traitListScroll, traitListInnerRect);

                float scrollY = 0f;
                foreach (WeaponTraitDef trait in compatibleTraits)
                {
                    if (ShouldShowTrait(trait))
                        DrawTraitRow(traitListInnerRect.x, ref scrollY,
                            traitListInnerRect.width, trait);
                }

                Widgets.EndScrollView();
            }
            curY += traitListHeight;

            // Divider + toggle at the bottom
            curY += 4f;
            GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Widgets.DrawLineHorizontal(rect.x, curY, rect.width);
            GUI.color = Color.white;
            curY += 4f;

            curY += checkMargin;
            Rect checkboxRect = new Rect(rect.x + checkMargin, curY, checkSize, checkSize);
            Widgets.Checkbox(checkboxRect.x, checkboxRect.y, ref hideNegativeTraits, checkSize);
            Rect checkLabelRect = new Rect(
                checkboxRect.xMax + 4f, curY, rect.width - checkSize - checkMargin - 4f, checkSize);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(checkLabelRect, "UWU_HideNegativeTraits".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // Returns a centered empty-state message attributing the empty list to
        // whichever filter is responsible (search > progression > hide-negative
        // > fallback). Search takes priority because it's an active user input;
        // progression beats hide-negative because it's the harder filter to
        // discover and act on.
        private string GetEmptyTraitListMessage()
        {
            // DrawTraitsTab returns early when compatibleTraits is empty, so
            // every branch here can assume at least one compatible trait exists
            // and reason purely about which filter excluded the visible set.
            if (traitSearchWidget.filter.Active)
                return "UWU_NoTraitsMatchSearch".Translate();

            // Walk filters in dependency order to identify the responsible one.
            // countAfterProgression: traits that pass progression alone (or are originals).
            // countAfterAll: traits that also pass hide-negative.
            int countAfterProgression = 0;
            int countAfterAll = 0;
            foreach (WeaponTraitDef trait in compatibleTraits)
            {
                bool isOriginalOrDesired =
                    originalTraits.Contains(trait) || desiredTraits.Contains(trait);

                bool passesProgression = progressionPool == null
                    || progressionPool.IsVisible(trait)
                    || isOriginalOrDesired;
                if (!passesProgression)
                    continue;
                countAfterProgression++;

                bool passesNegative = !hideNegativeTraits
                    || !TraitCostUtility.IsNegativeTrait(trait)
                    || isOriginalOrDesired;
                if (!passesNegative)
                    continue;
                countAfterAll++;
            }

            if (progressionPool != null
                && countAfterProgression == 0
                && compatibleTraits.Count > 0)
                return "UWU_NoTraitsDiscovered".Translate();

            if (hideNegativeTraits && countAfterAll == 0 && countAfterProgression > 0)
                return "UWU_NoTraitsAfterHideNegative".Translate();

            return "UWU_NoTraitsAvailable".Translate();
        }

        private bool ShouldShowTrait(WeaponTraitDef trait)
        {
            // Progression filter: hide traits with no player-discoverable source.
            // Traits already on this weapon stay visible regardless — otherwise the
            // player couldn't see what they're removing. Hostile-only traits also
            // stay visible (they render disabled with a rejection reason instead).
            if (progressionPool != null
                && !progressionPool.IsVisible(trait)
                && !originalTraits.Contains(trait)
                && !desiredTraits.Contains(trait))
                return false;

            // Active search overrides hide-negative so explicit matches aren't filtered out
            if (traitSearchWidget.filter.Active)
                return traitSearchWidget.filter.Matches(trait.label);

            if (!hideNegativeTraits)
                return true;
            if (!TraitCostUtility.IsNegativeTrait(trait))
                return true;
            // Always show negative traits that are selected or on the weapon
            return desiredTraits.Contains(trait) || originalTraits.Contains(trait);
        }

        // Returns a progression-mode rejection reason for the trait, or null
        // when the progression filter doesn't reject it. Stacks before the
        // regular validity rejection — a hostile-only trait is rejected even
        // when it would otherwise be addable.
        private string GetProgressionRejection(WeaponTraitDef trait)
        {
            if (progressionPool == null)
                return null;
            // Selected/original traits bypass progression — the player already has them.
            if (desiredTraits.Contains(trait) || originalTraits.Contains(trait))
                return null;
            if (!progressionPool.HasNonHostileSource(trait))
                return "UWU_OnlyOnHostiles".Translate();
            return null;
        }

        private void DrawTraitRow(float x, ref float curY, float width, WeaponTraitDef trait)
        {
            Rect rowRect = new Rect(x, curY, width, TraitRowHeight);
            string rejection = GetProgressionRejection(trait)
                ?? TraitValidationUtility.GetRejectionReason(desiredTraits, trait);
            bool isSelected = desiredTraits.Contains(trait);
            bool isDisabled = rejection != null;
            bool isClickable = isSelected || !isDisabled;

            // Highlight on hover for clickable traits
            if (isClickable && Mouse.IsOver(rowRect))
                Widgets.DrawHighlight(rowRect);

            // Selected highlight
            if (isSelected)
                Widgets.DrawBoxSolid(rowRect, new Color(0.35f, 0.35f, 0.35f, 0.4f));

            Color prevColor = GUI.color;
            if (isDisabled && !isSelected)
                GUI.color = new Color(0.5f, 0.5f, 0.5f);

            // Trait label — yellow when removing this trait from the weapon being
            // customized would empty the player's pool of available sources for it.
            // Save/restore tightly around the label so cost icons + rejection text
            // keep their normal coloring.
            bool isLastSource = progressionPool != null
                && progressionPool.IsLastNonHostileSource(trait, originalTraits);
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect labelRect = new Rect(rowRect.x + 4f, rowRect.y,
                rowRect.width * 0.35f, rowRect.height);
            Color preLabelColor = GUI.color;
            if (isLastSource)
                GUI.color = ColorLibrary.Yellow;
            Widgets.Label(labelRect, trait.LabelCap);
            GUI.color = preLabelColor;

            // Cost icons (right-aligned) — shows the delta from toggling this row:
            // - Selected original: removal preview (refund green / neg cost hypothetical red)
            // - Unselected original: no cost (re-adding is free, trait is already on weapon)
            // - Selected new: addition cost, red if insufficient
            // - Unselected new: hypothetical addition cost, red if hypothetically unaffordable
            Rect costRect = new Rect(rowRect.x + rowRect.width * 0.7f, rowRect.y,
                rowRect.width * 0.3f - 4f, rowRect.height);
            bool isOriginal = originalTraits.Contains(trait);
            if (isSelected && isOriginal)
            {
                // Clicking would remove — show what removal would cost/refund
                List<ThingDefCountClass> removalResult = GetRemovalCost(trait);
                if (TraitCostUtility.IsNegativeTrait(trait))
                {
                    DrawCostIcons(costRect, removalResult, rightAlign: true,
                        insufficientResources: GetHypotheticalInsufficient(removalResult));
                }
                else
                {
                    DrawCostIcons(costRect, removalResult, rightAlign: true,
                        greenQuantities: true);
                }
            }
            else if (!isSelected && isOriginal)
            {
                // Clicking would re-add — free, trait is already on the weapon
            }
            else if (isSelected)
            {
                List<ThingDefCountClass> costs = GetAdditionCost(trait);
                DrawCostIcons(costRect, costs, rightAlign: true,
                    insufficientResources: insufficientResources);
            }
            else
            {
                List<ThingDefCountClass> costs = GetAdditionCost(trait);
                DrawCostIcons(costRect, costs, rightAlign: true,
                    insufficientResources: GetHypotheticalInsufficient(costs));
            }

            // Rejection reason (centered in the middle zone between label and costs)
            if (isDisabled && !isSelected)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.4f, 0.4f);
                Rect rejRect = new Rect(labelRect.xMax, rowRect.y,
                    costRect.x - labelRect.xMax, rowRect.height);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rejRect, rejection);
                Text.Font = GameFont.Small;
            }

            GUI.color = prevColor;
            Text.Anchor = TextAnchor.UpperLeft;

            // Tooltip with trait description and stat effects. The "last source"
            // warning gets its own yellow tooltip box stacked alongside, mirroring
            // the LHS chip behavior so the visual cue and the explanation share an
            // identity even after the player removes the trait via this row.
            if (Mouse.IsOver(rowRect))
            {
                string tooltip = BuildTraitTooltip(trait);
                if (!string.IsNullOrEmpty(tooltip))
                    TooltipHandler.TipRegion(rowRect, tooltip);
                if (isLastSource)
                    TooltipHandler.TipRegion(rowRect,
                        "<color=#ffff14>" + "UWU_LastTraitSourceWarning".Translate() + "</color>");
            }

            // Click to add or remove
            if (isSelected)
            {
                if (Widgets.ButtonInvisible(rowRect)
                    && TraitValidationUtility.CanRemoveTrait(desiredTraits, trait))
                {
                    desiredTraits.Remove(trait);
                    OnTraitsChanged();
                }
            }
            else if (!isDisabled && Widgets.ButtonInvisible(rowRect))
            {
                desiredTraits.Add(trait);
                OnTraitsChanged();
            }

            curY += TraitRowHeight + TraitRowGap;
        }

        private string BuildTraitTooltip(WeaponTraitDef trait)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(trait.description))
                parts.Add(trait.description);

            var effectLines = new List<string>();

            if (trait.statOffsets != null)
            {
                foreach (StatModifier mod in trait.statOffsets)
                    effectLines.Add("  " + mod.stat.LabelCap + ": "
                        + mod.stat.ValueToString(mod.value, ToStringNumberSense.Offset));
            }

            if (trait.statFactors != null)
            {
                foreach (StatModifier mod in trait.statFactors)
                    effectLines.Add("  " + mod.stat.LabelCap + ": "
                        + mod.stat.ValueToString(mod.value, ToStringNumberSense.Factor));
            }

            if (trait.equippedStatOffsets != null)
            {
                foreach (StatModifier mod in trait.equippedStatOffsets)
                    effectLines.Add("  " + mod.stat.LabelCap + ": "
                        + mod.stat.ValueToString(mod.value, ToStringNumberSense.Offset));
            }

            if (trait.damageDefOverride != null)
                effectLines.Add("  " + "UWU_DamageType".Translate() + ": " + trait.damageDefOverride.LabelCap);

            if (trait.extraDamages != null)
            {
                foreach (ExtraDamage dmg in trait.extraDamages)
                    effectLines.Add("  " + "UWU_ExtraDamage".Translate() + ": "
                        + dmg.amount + " " + dmg.def.LabelCap);
            }

            if (trait.burstShotSpeedMultiplier != 1f)
                effectLines.Add("  " + "UWU_BurstSpeed".Translate() + ": x"
                    + trait.burstShotSpeedMultiplier.ToString("0.##"));

            if (trait.burstShotCountMultiplier != 1f)
                effectLines.Add("  " + "UWU_BurstCount".Translate() + ": x"
                    + trait.burstShotCountMultiplier.ToString("0.##"));

            if (trait.additionalStoppingPower > 0f)
                effectLines.Add("  " + "UWU_StoppingPower".Translate() + ": +"
                    + trait.additionalStoppingPower.ToString("0.##"));

            if (trait.ignoresAccuracyMaluses)
                effectLines.Add("  " + "UWU_IgnoresAccuracyPenalties".Translate());

            if (effectLines.Count > 0)
                parts.Add("UWU_Effects".Translate() + "\n" + string.Join("\n", effectLines));

            return string.Join("\n\n", parts);
        }
    }
}

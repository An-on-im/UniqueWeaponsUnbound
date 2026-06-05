using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    public partial class Dialog_WeaponCustomization
    {
        private const int PreviewRTSize = 256;
        private const int TextureGridRTSize = 128;

        // Cached preview render — rebuilt only when preview state changes
        private RenderTexture previewRT;
        private int cachedPreviewTextureIndex = -1;
        private ColorDef cachedPreviewColor;
        private ThingDef cachedPreviewDef;

        // Cached texture variant grid previews — rebuilt when color/def changes
        private RenderTexture[] textureVariantPreviews;
        private ColorDef cachedTextureGridColor;
        private ThingDef cachedTextureGridDef;

        // --- Left pane: weapon preview ---

        private void DrawWeaponPreview(Rect rect)
        {
            float curY = rect.y + 10f;

            // Weapon icon — reflects desired texture variant and effective color
            float iconSize = Mathf.Min(rect.width - 20f, rect.height * 0.4f);
            Rect iconRect = new Rect(
                rect.x + (rect.width - iconSize) / 2f,
                curY,
                iconSize,
                iconSize);
            DrawPreviewIcon(iconRect);

            curY = iconRect.yMax + 8f;

            // Name input field
            DrawNameRow(rect.x + 8f, ref curY, rect.width - 16f);

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 20f;

            // Bottom-aligned cost and refund summary (always visible)
            {
                float costRowHeight = CostIconSize + 8f;
                float bottomPadding = 6f;

                // Reserve space for the two summary rows so chips can scroll above them
                float summaryHeight = costRowHeight * 2f + bottomPadding;
                float chipsAreaHeight = Mathf.Max(0f, rect.yMax - curY - summaryHeight - 4f);

                if (desiredTraits.Count > 0 && chipsAreaHeight > 0f)
                {
                    Rect chipsOuterRect = new Rect(
                        rect.x + 8f, curY, rect.width - 16f, chipsAreaHeight);
                    float chipStride = TraitRowHeight + 2f;
                    float chipsContentHeight = desiredTraits.Count * chipStride;
                    bool needsScroll = chipsContentHeight > chipsAreaHeight;
                    float innerWidth = needsScroll
                        ? chipsOuterRect.width - 16f
                        : chipsOuterRect.width;
                    Rect chipsInnerRect = new Rect(0f, 0f, innerWidth, chipsContentHeight);

                    Widgets.BeginScrollView(chipsOuterRect, ref desiredTraitsScroll, chipsInnerRect);

                    float chipY = 0f;
                    foreach (WeaponTraitDef trait in desiredTraits)
                    {
                        Rect chipRect = new Rect(0f, chipY, innerWidth, TraitRowHeight);

                        // Chip background with hover highlight
                        bool hovered = Mouse.IsOver(chipRect);
                        Widgets.DrawBoxSolid(chipRect, hovered
                            ? new Color(0.3f, 0.3f, 0.3f, 0.5f)
                            : new Color(0.2f, 0.2f, 0.2f, 0.4f));

                        // Label — yellow when removing this trait would empty the
                        // player's pool of available sources for it (progression mode).
                        bool isLastSource = progressionPool != null
                            && progressionPool.IsLastNonHostileSource(trait, originalTraits);
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Rect labelRect = new Rect(
                            chipRect.x + 4f, chipRect.y,
                            chipRect.width * 0.5f, chipRect.height);
                        Color prevLabelColor = GUI.color;
                        if (isLastSource)
                            GUI.color = ColorLibrary.Yellow;
                        Widgets.Label(labelRect, trait.LabelCap);
                        GUI.color = prevLabelColor;

                        // Cost icons (right-aligned) — only for newly added traits
                        if (!originalTraits.Contains(trait))
                        {
                            List<ThingDefCountClass> chipCosts = GetAdditionCost(trait);
                            Rect chipCostRect = new Rect(
                                labelRect.xMax, chipRect.y,
                                chipRect.xMax - labelRect.xMax - 4f, chipRect.height);
                            DrawCostIcons(chipCostRect, chipCosts, rightAlign: true,
                                insufficientResources: insufficientResources);
                        }

                        Text.Anchor = TextAnchor.UpperLeft;

                        // Tooltip (same as traits tab). The "last source" warning
                        // gets its own tooltip box stacked alongside, so it reads as
                        // a distinct alert rather than being lost at the bottom of
                        // a long stat block.
                        string tooltip = BuildTraitTooltip(trait);
                        if (!string.IsNullOrEmpty(tooltip))
                            TooltipHandler.TipRegion(chipRect, tooltip);
                        if (isLastSource)
                        {
                            // Color the tooltip body to match the chip's yellow label,
                            // so the visual cue and the explanatory tip share an identity.
                            // Hex matches ColorLibrary.Yellow (#ffff14).
                            TooltipHandler.TipRegion(chipRect,
                                "<color=#ffff14>" + "UWU_LastTraitSourceWarning".Translate() + "</color>");
                        }

                        // Click: switch to traits tab and scroll trait into view
                        if (Widgets.ButtonInvisible(chipRect))
                        {
                            activeTab = 0;
                            int traitIndex = compatibleTraits.IndexOf(trait);
                            if (traitIndex >= 0)
                                traitListScroll.y = traitIndex * (TraitRowHeight + TraitRowGap);
                        }

                        chipY += chipStride;
                    }

                    Widgets.EndScrollView();
                }

                bool hasSurplus = currentSurplus != null && currentSurplus.Count > 0;
                bool hasNetCost = currentNetCost != null && currentNetCost.Count > 0;

                // Stack from bottom: refund row, net cost row
                float bottomY = rect.yMax - bottomPadding;

                // Net refund row
                Rect refundArea = new Rect(
                    rect.x + 8f, bottomY - costRowHeight,
                    rect.width - 16f, costRowHeight);

                Text.Anchor = TextAnchor.MiddleLeft;
                if (!hasSurplus)
                    GUI.color = Color.gray;
                string refundLabel = "UWU_NetRefund".Translate();
                float refundLabelWidth = Text.CalcSize(refundLabel).x;
                Widgets.Label(
                    new Rect(refundArea.x, refundArea.y,
                        refundLabelWidth, refundArea.height),
                    refundLabel);

                if (hasSurplus)
                {
                    DrawCostIcons(
                        new Rect(refundArea.x + refundLabelWidth, refundArea.y,
                            refundArea.width - refundLabelWidth, refundArea.height),
                        currentSurplus,
                        greenQuantities: true,
                        maxVisible: 5);
                    TooltipHandler.TipRegion(refundArea,
                        refundLabel + FormatCostList(currentSurplus));
                }
                else
                {
                    Widgets.Label(
                        new Rect(refundArea.x + refundLabelWidth, refundArea.y,
                            refundArea.width - refundLabelWidth, refundArea.height),
                        "UWU_RefundNone".Translate());
                    GUI.color = Color.white;
                }
                Text.Anchor = TextAnchor.UpperLeft;

                // Net cost row above refund
                Rect netCostArea = new Rect(
                    rect.x + 8f, refundArea.y - costRowHeight,
                    rect.width - 16f, costRowHeight);

                Text.Anchor = TextAnchor.MiddleLeft;
                if (hasNetCost)
                {
                    string costLabel = "UWU_NetCost".Translate();
                    float labelWidth = Text.CalcSize(costLabel).x;
                    Widgets.Label(
                        new Rect(netCostArea.x, netCostArea.y, labelWidth, netCostArea.height),
                        costLabel);

                    DrawCostIcons(
                        new Rect(netCostArea.x + labelWidth, netCostArea.y,
                            netCostArea.width - labelWidth, netCostArea.height),
                        currentNetCost,
                        insufficientResources: insufficientResources,
                        maxVisible: 5);
                    TooltipHandler.TipRegion(netCostArea,
                        costLabel + FormatCostList(currentNetCost));
                }
                else
                {
                    string costPrefix = "UWU_NetCost".Translate();
                    float prefixWidth = Text.CalcSize(costPrefix).x;
                    Widgets.Label(
                        new Rect(netCostArea.x, netCostArea.y, prefixWidth, netCostArea.height),
                        costPrefix);
                    Color prevFreeColor = GUI.color;
                    GUI.color = new Color(0.4f, 0.8f, 0.4f);
                    Widgets.Label(
                        new Rect(netCostArea.x + prefixWidth, netCostArea.y,
                            netCostArea.width - prefixWidth, netCostArea.height),
                        "UWU_CostFree".Translate());
                    GUI.color = prevFreeColor;
                }
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private static string FormatCostList(List<ThingDefCountClass> costs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (ThingDefCountClass cost in costs)
                sb.Append("\n  ").Append(cost.thingDef.LabelCap).Append(" x").Append(cost.count);
            return sb.ToString();
        }

        private void DrawPreviewIcon(Rect rect)
        {
            ThingDef resultDef = ResultingDef;
            ColorDef effectiveColor = IsRevertedToBase ? null : EffectiveColor;

            bool needsRebuild = previewRT == null
                || cachedPreviewTextureIndex != desiredTextureIndex
                || cachedPreviewColor != effectiveColor
                || cachedPreviewDef != resultDef;

            // Rebuild during Layout to avoid disrupting Repaint's active rendering.
            // Graphics.Blit changes RenderTexture.active, which during Repaint would
            // redirect subsequent UI draws into our texture instead of the screen.
            if (needsRebuild && Event.current.type == EventType.Layout)
            {
                RebuildPreviewRT(resultDef, effectiveColor);
                cachedPreviewTextureIndex = desiredTextureIndex;
                cachedPreviewColor = effectiveColor;
                cachedPreviewDef = resultDef;
            }

            if (previewRT != null)
                GUI.DrawTexture(rect, previewRT, ScaleMode.ScaleToFit, true);
            else
                Widgets.ThingIcon(rect, resultDef);
        }

        private void RebuildPreviewRT(ThingDef resultDef, ColorDef colorDef)
        {
            DestroyPreviewRT();
            previewRT = BuildVariantPreview(resultDef, colorDef, desiredTextureIndex, PreviewRTSize);
        }

        /// <summary>
        /// Builds a RenderTexture preview for a specific texture variant of the weapon.
        /// Shared by the main preview icon and the texture variant grid.
        /// </summary>
        private RenderTexture BuildVariantPreview(
            ThingDef resultDef, ColorDef colorDef, int textureIndex, int rtSize)
        {
            Graphic graphic = resultDef.graphicData?.Graphic;
            if (graphic == null)
                return null;

            // Recolor the top-level graphic exactly as the engine does for a Thing
            // (GraphicData.GraphicColoredFor): hand its own shader, the prospective
            // first color, and the weapon's own second color to GetColoredVersion.
            // Dispatching on the top-level graphic runs the weapon's own graphic
            // class (e.g. a Graphic_Random subclass that preserves colorTwo through
            // the CutoutComplex green mask) — we invoke that recolor logic rather
            // than reimplementing it, so any mod's color handling comes through for
            // free within the engine's two-color contract. Shader and color two
            // match in-game; only color one is the prospective dialog choice.
            if (colorDef != null)
                graphic = graphic.GetColoredVersion(
                    graphic.Shader, colorDef.color, weapon?.DrawColorTwo ?? Color.white);

            // Select the texture variant from the (recolored) graphic, mirroring
            // Graphic_Random.SubGraphicFor at draw time. GetColoredVersion preserves
            // the wrapper types, so unwrap rotation then index into the variants.
            if (graphic is Graphic_RandomRotated rotated)
                graphic = rotated.SubGraphic;
            if (graphic is Graphic_Random random)
                graphic = random.SubGraphicAtIndex(textureIndex);

            Material mat = graphic.MatSingle;
            Texture mainTex = mat?.mainTexture;
            if (mainTex == null)
                return null;

            RenderTexture rt = new RenderTexture(rtSize, rtSize, 0, RenderTextureFormat.ARGB32);

            // Save and restore RenderTexture.active around the entire operation.
            // Graphics.Blit sets it to the destination and does NOT restore it —
            // leaving it set would redirect all subsequent UI rendering into our texture.
            RenderTexture prev = RenderTexture.active;

            // Clear to transparent so clipped pixels (alpha < cutoff) stay transparent
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);

            // Blit through the material's shader — CutoutComplex reads the mask
            // texture to selectively apply the color, matching in-game rendering
            Graphics.Blit(mainTex, rt, mat);

            RenderTexture.active = prev;
            return rt;
        }

        private void EnsureTextureVariantPreviews()
        {
            ThingDef resultDef = ResultingDef;
            ColorDef effectiveColor = IsRevertedToBase ? null : EffectiveColor;

            bool needsRebuild = textureVariantPreviews == null
                || cachedTextureGridColor != effectiveColor
                || cachedTextureGridDef != resultDef;

            if (!needsRebuild || Event.current.type != EventType.Layout)
                return;

            DestroyTextureVariantPreviews();
            textureVariantPreviews = new RenderTexture[textureVariantCount];

            for (int i = 0; i < textureVariantCount; i++)
                textureVariantPreviews[i] = BuildVariantPreview(
                    resultDef, effectiveColor, i, TextureGridRTSize);

            cachedTextureGridColor = effectiveColor;
            cachedTextureGridDef = resultDef;
        }

        private void DestroyPreviewRT()
        {
            if (previewRT != null)
            {
                previewRT.Release();
                UnityEngine.Object.Destroy(previewRT);
                previewRT = null;
            }
        }

        private void DestroyTextureVariantPreviews()
        {
            if (textureVariantPreviews != null)
            {
                foreach (RenderTexture rt in textureVariantPreviews)
                {
                    if (rt != null)
                    {
                        rt.Release();
                        UnityEngine.Object.Destroy(rt);
                    }
                }
                textureVariantPreviews = null;
            }
        }

        public override void PreClose()
        {
            base.PreClose();
            DestroyPreviewRT();
            DestroyTextureVariantPreviews();
        }
    }
}

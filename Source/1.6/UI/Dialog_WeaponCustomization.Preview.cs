using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniqueWeaponsUnbound
{
    public partial class Dialog_WeaponCustomization
    {
        private const int PreviewRTSize = 256;
        private const int TextureGridRTSize = 128;

        // Cached preview render — rebuilt only when preview state changes.
        // The trait snapshot is part of the key because appearance is now
        // trait-dependent: a trait can drive color two (or any override reachable
        // through the thing's graphic), so toggling one must rebuild even when the
        // def and color-one choice are unchanged.
        private RenderTexture previewRT;
        private int cachedPreviewTextureIndex = -1;
        private ColorDef cachedPreviewColor;
        private ThingDef cachedPreviewDef;
        private List<WeaponTraitDef> cachedPreviewTraits;

        // One prospective Thing reused across rebuilds (re-made only on def change).
        // ThingMaker.MakeThing mutates global sim state — Thing.PostMake draws a
        // UniqueIDsManager id and PostPostMake rolls off the global Rand — so caching
        // it bounds those draws to def changes instead of firing on every rebuild.
        private Thing previewThing;

        // Cached texture variant grid previews — rebuilt when color/def/traits change
        private RenderTexture[] textureVariantPreviews;
        private ColorDef cachedTextureGridColor;
        private ThingDef cachedTextureGridDef;
        private List<WeaponTraitDef> cachedTextureGridTraits;

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

            // Vanilla "i" stats button — opens the info card for the prospective
            // weapon, so the player reads final stats rather than summing trait
            // modifiers by hand. The thing's identity state is stamped when it's
            // (re)built (see BuildPreviewGraphic); the desired name is stamped
            // here instead because name edits never trigger a rebuild (they
            // don't affect appearance). SetName also mirrors the name into
            // CompArt.Title, matching what accepting the customization does.
            //
            // Placement: right edge flush with the pane itself, bottom flush
            // with the right pane's tab headers — both panes share
            // contentRect.y, and the tabs DrawControlsPanel hangs above its
            // menu section bottom out at rect.y + 8f + TabBarHeight.
            if (previewThing != null)
            {
                WeaponModificationUtility.SetName(previewThing, desiredName);
                Widgets.InfoCardButton(
                    rect.xMax - InfoCardButtonSize,
                    rect.y + 8f + TabBarHeight - InfoCardButtonSize,
                    previewThing);
            }

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
                || cachedPreviewDef != resultDef
                || !SameTraits(cachedPreviewTraits, desiredTraits);

            // Rebuild during Layout to avoid disrupting Repaint's active rendering.
            // Graphics.Blit changes RenderTexture.active, which during Repaint would
            // redirect subsequent UI draws into our texture instead of the screen.
            if (needsRebuild && Event.current.type == EventType.Layout)
            {
                RebuildPreviewRT(resultDef, effectiveColor);
                cachedPreviewTextureIndex = desiredTextureIndex;
                cachedPreviewColor = effectiveColor;
                cachedPreviewDef = resultDef;
                cachedPreviewTraits = new List<WeaponTraitDef>(desiredTraits);
            }

            if (previewRT != null)
                GUI.DrawTexture(rect, previewRT, ScaleMode.ScaleToFit, true);
            else
                Widgets.ThingIcon(rect, resultDef);
        }

        private void RebuildPreviewRT(ThingDef resultDef, ColorDef colorDef)
        {
            DestroyPreviewRT();
            Graphic topLevel = BuildPreviewGraphic(resultDef, colorDef);
            previewRT = BuildVariantPreview(topLevel, desiredTextureIndex, PreviewRTSize);
        }

        // Resolves the weapon's top-level (collection-level) graphic for a
        // prospective customization state — the desired def, color, and trait
        // set — by building a Thing in that state and asking it, rather than
        // predicting the appearance by hand.
        //
        // We let the actual object describe itself: Thing.Graphic resolves
        // through GraphicData.GraphicColoredFor using the thing's own
        // DrawColor/DrawColorTwo, so the weapon's own Thing/Comp graphic
        // overrides run against the prospective trait list. That keeps the
        // preview decoupled from how a given weapon (vanilla or a downstream
        // mod) maps state to appearance — any override reachable through the
        // thing's graphic comes through for free, with no knowledge of its
        // mechanism here. (The one ceiling: an override that lives purely in a
        // draw-time patch and never changes the thing's graphic can't be
        // reconstructed by anything short of invoking that draw path.)
        //
        // One override needs a nudge rather than coming through for free: VEF /
        // Alpha Armoury's trait-driven graphic swap does change the thing's
        // graphicInt, but only recomputes on equip/load, neither of which fires
        // here. VEFWeaponTraitGraphicsIntegration.RefreshTraitGraphic runs that
        // recompute against the prospective traits so it lands in graphicInt
        // before we read Graphic — still within the "ask the object" contract,
        // just triggering the resolution VEF defers.
        //
        // For appearance, only the trait list and the comp's color field need
        // setting: color one is read live from CompUniqueWeapon.ForceColor
        // (just that field — no trait scan, no Setup() cache), and color two is
        // derived from the trait list (+ stuff) by the weapon's own
        // DrawColorTwo. Abilities/verbs don't affect appearance, so the heavier
        // AddTrait wiring is skipped — the list is replaced directly. Beyond
        // appearance, the thing also backs the preview's info card, so when it
        // is (re)made the original weapon's identity state (quality, hitpoints,
        // biocoding, art, relic status) is stamped on via WeaponDefConversion's
        // copy helpers — copy semantics, not the conversion pipeline's
        // ownership transfers, so the live weapon's state is never disturbed.
        //
        // Building a Thing mutates global sim state, which the old graphic-only
        // path never touched: Thing.PostMake pulls a UniqueIDsManager id and
        // CompUniqueWeapon.PostPostMake rolls random traits/name/color off the
        // global Rand. Two guards keep that from leaking (a multiplayer desync
        // risk, since rebuilds run during GUI layout, off the synchronized
        // tick): the make is wrapped in Rand.Push/PopState so the throwaway
        // rolls don't perturb the shared Rand stream, and the Thing is cached
        // on previewThing and re-made only when the result def changes — so the
        // id draw fires per def, not per rebuild. Re-stamping traits/color
        // below touches no global state. The cached thing is never spawned,
        // never scribed, and never destroyed — simply dropped with the dialog.
        // That lifecycle is also what makes the identity stamp's shared
        // references (art TaleReference, relic precept, coded pawn) safe: the
        // destroy and save paths, the only places a shared reference could tear
        // down or fork state the real weapon still owns, never run. Destroy()
        // must NOT be added here — it would fire CompArt.PostDestroy /
        // Notify_ThingLost against the live weapon's tale and precept.
        private Graphic BuildPreviewGraphic(ThingDef resultDef, ColorDef colorDef)
        {
            if (resultDef?.graphicData == null)
                return null;

            if (previewThing == null || previewThing.def != resultDef)
            {
                // Mirror WeaponDefConversion: carry the live weapon's material across
                // (color two's stuff tint depends on it), falling back to the default
                // so a stuffable target is never handed a null stuff.
                ThingDef stuff = resultDef.MadeFromStuff
                    ? (weapon.Stuff ?? GenStuff.DefaultStuffFor(resultDef))
                    : null;

                // Contain PostMake/PostPostMake's Rand draws — we overwrite the rolled
                // state below, so the values don't matter, but the global stream must
                // not advance (see method remarks).
                Rand.PushState();
                try
                {
                    previewThing = ThingMaker.MakeThing(resultDef, stuff);
                }
                finally
                {
                    Rand.PopState();
                }

                // Mirror ConvertWeaponDef's identity handling so the info card
                // opened from the preview reads as the customized weapon will:
                // scrub PostPostMake's rolled unique state, then stamp the
                // original's quality (null art source — no InitializeArt roll),
                // hitpoint percentage, biocoding, art, and relic status. These
                // are the copy-semantics halves of the conversion transfers —
                // the original keeps ownership of the shared references (art
                // TaleReference, relic precept), which the preview thing's
                // lifecycle makes safe (see method remarks). None of it touches
                // global state, so no Rand guard is needed. Once per make:
                // everything stamped here is immutable while the dialog is open.
                WeaponModificationUtility.ClearAutoGeneratedUniqueState(previewThing);
                WeaponDefConversion.CopyQuality(weapon, previewThing);
                WeaponDefConversion.CopyHitPointsPercent(weapon, previewThing);
                WeaponDefConversion.CopyBiocodeState(weapon, previewThing);
                WeaponDefConversion.CopyArt(weapon, previewThing);
                WeaponDefConversion.CopyRelicStatus(weapon, previewThing);
            }

            CompUniqueWeapon comp = previewThing.TryGetComp<CompUniqueWeapon>();
            if (comp != null)
            {
                // Replace PostPostMake's random roll with the prospective trait set.
                List<WeaponTraitDef> traits = comp.TraitsListForReading;
                traits.Clear();
                traits.AddRange(desiredTraits);

                // Write color one and invalidate the cached graphic (SetColor fires
                // Notify_ColorChanged), so Graphic rebuilds against the prospective
                // state below. Color two is left to the thing's own DrawColorTwo.
                WeaponModificationUtility.SetColor(previewThing, colorDef);
            }

            // Keep the thing-side texture variant in step with the preview. The
            // dialog renders variants by indexing into the graphic directly, but
            // the info card's icon resolves through the thing (Widgets.ThingIcon
            // → ExtractInnerGraphicFor → Graphic_Random.SubGraphicFor), which
            // without an override falls back to hashing the throwaway
            // thingIDNumber — a random variant, not the desired one. Runs every
            // rebuild because the desired index is preview state, unlike the
            // stamped identity above.
            WeaponModificationUtility.SetTextureIndex(previewThing, desiredTextureIndex);

            // Drive VEF / Alpha Armoury's trait-driven graphic override against the
            // prospective trait set, exactly as an equip would. It writes the
            // resolved graphic into the thing's graphicInt (read via Graphic below);
            // a no-op when VEF is absent or no trait overrides this def, leaving the
            // vanilla graphic SetColor just invalidated. Must run after SetColor — it
            // reads the comp's color-one to tint the override.
            VEFWeaponTraitGraphicsIntegration.RefreshTraitGraphic(previewThing);

            return previewThing.Graphic;
        }

        // Blits one texture variant of a prebuilt, already-colored top-level
        // graphic into a fresh RenderTexture. Shared by the main preview icon
        // and the texture variant grid (which reuses one graphic across all
        // variants).
        private RenderTexture BuildVariantPreview(Graphic topLevel, int textureIndex, int rtSize)
        {
            if (topLevel == null)
                return null;

            // Select the texture variant, mirroring Graphic_Random.SubGraphicFor at
            // draw time. The coloring preserves the wrapper types, so unwrap rotation
            // then index into the variants.
            Graphic graphic = topLevel;
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
                || cachedTextureGridDef != resultDef
                || !SameTraits(cachedTextureGridTraits, desiredTraits);

            if (!needsRebuild || Event.current.type != EventType.Layout)
                return;

            DestroyTextureVariantPreviews();
            textureVariantPreviews = new RenderTexture[textureVariantCount];

            // The variants share def/color/traits and differ only by index, so
            // build the prospective graphic once and index into it per tile.
            Graphic topLevel = BuildPreviewGraphic(resultDef, effectiveColor);
            for (int i = 0; i < textureVariantCount; i++)
                textureVariantPreviews[i] = BuildVariantPreview(
                    topLevel, i, TextureGridRTSize);

            cachedTextureGridColor = effectiveColor;
            cachedTextureGridDef = resultDef;
            cachedTextureGridTraits = new List<WeaponTraitDef>(desiredTraits);
        }

        // Ordered equality for the two preview caches' trait snapshots. Order
        // matters — color resolution is order-sensitive (e.g. "last forced
        // color wins" / "first body-color trait wins"). A null cached snapshot
        // (first build) never matches, forcing the initial rebuild.
        private static bool SameTraits(List<WeaponTraitDef> cached, List<WeaponTraitDef> current)
        {
            if (cached == null)
                return false;
            if (cached.Count != current.Count)
                return false;
            for (int i = 0; i < cached.Count; i++)
            {
                if (cached[i] != current[i])
                    return false;
            }
            return true;
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

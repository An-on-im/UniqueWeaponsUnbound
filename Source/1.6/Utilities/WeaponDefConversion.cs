using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Transforms a weapon Thing into a different ThingDef while preserving
    /// identity-bearing properties: stuff (material), quality, hitpoint
    /// percentage, texture override, biocoding, authored/relic art, and (when
    /// applicable) Ideology relic status. Used during customization at the 0↔1
    /// trait boundary to swap between a weapon's base def and its unique
    /// counterpart.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class WeaponDefConversion
    {
        // Ideology DLC: Precept_Relic.generatedRelic (private Thing).
        // Resolved once at startup; null if Ideology is not installed.
        // A null value when Ideology IS installed means the field surface has
        // drifted — the static ctor below logs that as an error so a silently
        // stale relic→thing pointer doesn't corrupt a relic during def
        // conversion (see TransferRelicStatus).
        private static readonly FieldInfo GeneratedRelicField =
            GenTypes.GetTypeInAnyAssembly("RimWorld.Precept_Relic")
                ?.GetField("generatedRelic", BindingFlags.NonPublic | BindingFlags.Instance);

        // CompArt persisted state (RimWorld core). No public setters exist for the
        // author or the backing TaleReference, so the three scribed fields are
        // moved wholesale during conversion to keep an authored/relic weapon's
        // title, author, and art description intact. See TransferArt.
        private static readonly FieldInfo ArtAuthorField = typeof(CompArt)
            .GetField("authorNameInt", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ArtTitleField = typeof(CompArt)
            .GetField("titleInt", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ArtTaleRefField = typeof(CompArt)
            .GetField("taleRef", BindingFlags.NonPublic | BindingFlags.Instance);

        // CompBiocodable persisted state (RimWorld core). The scribed fields are
        // copied directly rather than via CodeFor(pawn): CodeFor NREs on a null
        // pawn (so it can't reproduce an owner-discarded, label-only biocode) and
        // re-fires OnCodedFor side effects on the brand-new weapon. See
        // CopyBiocodeState.
        private static readonly FieldInfo BiocodedField = typeof(CompBiocodable)
            .GetField("biocoded", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo BiocodedPawnLabelField = typeof(CompBiocodable)
            .GetField("codedPawnLabel", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo BiocodedPawnField = typeof(CompBiocodable)
            .GetField("codedPawn", BindingFlags.NonPublic | BindingFlags.Instance);

        static WeaponDefConversion()
        {
            if (ModsConfig.IdeologyActive && GeneratedRelicField == null)
            {
                Log.Error("[Unique Weapons Unbound] Ideology active but "
                    + "Precept_Relic.generatedRelic could not be resolved via reflection; "
                    + "relic-flagged weapons that undergo a base<->unique def conversion "
                    + "during customization will leave the precept pointing at the "
                    + "destroyed pre-conversion weapon. RimWorld API may have changed.");
            }

            if (ArtAuthorField == null || ArtTitleField == null || ArtTaleRefField == null)
            {
                Log.Error("[Unique Weapons Unbound] CompArt private fields "
                    + "(authorNameInt/titleInt/taleRef) could not be resolved via reflection; "
                    + "authored/relic art (title, author, description) will be dropped when a "
                    + "weapon crosses the base<->unique boundary. RimWorld API may have changed.");
            }

            if (BiocodedField == null || BiocodedPawnLabelField == null || BiocodedPawnField == null)
            {
                Log.Error("[Unique Weapons Unbound] CompBiocodable private fields "
                    + "(biocoded/codedPawnLabel/codedPawn) could not be resolved via reflection; "
                    + "biocoding will be dropped when a weapon crosses the base<->unique boundary. "
                    + "RimWorld API may have changed.");
            }
        }

        /// <summary>
        /// Creates a new weapon Thing from targetDef, copying stuff, quality,
        /// hitpoints, texture, and biocoding from oldWeapon. If targetDef has
        /// CompUniqueWeapon (base→unique conversion), clears the auto-generated
        /// traits/name/color from PostPostMake(). Returns the new weapon.
        ///
        /// Does NOT destroy oldWeapon, and does NOT transfer art or relic status:
        /// both move a reference whose teardown must be sequenced against the old
        /// weapon's Destroy(), so the caller invokes <see cref="TransferArt"/> and
        /// <see cref="TransferRelicStatus"/> before destroying oldWeapon.
        /// </summary>
        public static Thing ConvertWeaponDef(Thing oldWeapon, ThingDef targetDef)
        {
            // Carry the material across when the target is stuffable. Passing the
            // stuff to MakeThing (rather than letting it default) preserves a
            // modded stuffable weapon's material and avoids vanilla's "madeFromStuff
            // but stuff=null" error log + silent reset to DefaultStuffFor. A
            // stuffable target with a stuffless source (degenerate) still falls
            // back to the default so we never hand MakeThing a null stuff.
            ThingDef stuff = targetDef.MadeFromStuff
                ? (oldWeapon.Stuff ?? GenStuff.DefaultStuffFor(targetDef))
                : null;
            Thing newWeapon = ThingMaker.MakeThing(targetDef, stuff);

            // Copy quality. Pass a null art source so SetQuality does NOT run
            // CompArt.InitializeArt — that would roll a fresh random title/tale on
            // the new weapon and strand its TaleReference. Authored art is moved
            // verbatim afterwards by TransferArt instead.
            if (oldWeapon.TryGetQuality(out QualityCategory quality))
            {
                CompQuality qualityComp = newWeapon.TryGetComp<CompQuality>();
                qualityComp?.SetQuality(quality, null);
            }

            // Copy hitpoints as a percentage of max. Read after stuff is set so the
            // percentage maps onto the stuff-adjusted MaxHitPoints.
            if (oldWeapon.MaxHitPoints > 0 && newWeapon.MaxHitPoints > 0)
            {
                float hpPct = (float)oldWeapon.HitPoints / oldWeapon.MaxHitPoints;
                newWeapon.HitPoints = (int)(newWeapon.MaxHitPoints * hpPct);
                if (newWeapon.HitPoints < 1)
                    newWeapon.HitPoints = 1;
            }

            // Copy texture index
            newWeapon.overrideGraphicIndex = oldWeapon.overrideGraphicIndex;

            // Carry biocoding across (no-op if neither weapon is biocodable).
            CopyBiocodeState(oldWeapon, newWeapon);

            // Scrub the random state PostPostMake leaves on a fresh unique weapon
            // (trait list, name, color, accuracy-malus cache, and any equippable-
            // ability comp wiring from a rolled ability trait). See the helper
            // for the full rationale — without the ability-comp scrub, abilities
            // from auto-rolled traits like SmokeLauncher persist as phantom
            // gizmos even after the trait list is cleared.
            WeaponModificationUtility.ClearAutoGeneratedUniqueState(newWeapon);

            return newWeapon;
        }

        /// <summary>
        /// Copies CompBiocodable state (biocoded flag, coded-pawn label, coded-pawn
        /// reference) from oldWeapon to newWeapon. No-op if either weapon lacks the
        /// comp or the old weapon isn't biocoded.
        ///
        /// Copies the scribed fields directly rather than calling CodeFor(pawn):
        /// CodeFor dereferences pawn.Name (NREs on the owner-discarded, label-only
        /// biocode that survives a save/load), and re-runs OnCodedFor side effects
        /// on a freshly created weapon. A faithful state transfer mirrors what
        /// Scribe persists, which is exactly these three fields.
        /// </summary>
        private static void CopyBiocodeState(Thing oldWeapon, Thing newWeapon)
        {
            CompBiocodable oldBio = oldWeapon.TryGetComp<CompBiocodable>();
            CompBiocodable newBio = newWeapon.TryGetComp<CompBiocodable>();
            if (oldBio == null || newBio == null || !oldBio.Biocoded)
                return;

            // Drift already logged at startup; bail rather than half-copy.
            if (BiocodedField == null || BiocodedPawnLabelField == null || BiocodedPawnField == null)
                return;

            BiocodedField.SetValue(newBio, true);
            BiocodedPawnLabelField.SetValue(newBio, BiocodedPawnLabelField.GetValue(oldBio));
            BiocodedPawnField.SetValue(newBio, BiocodedPawnField.GetValue(oldBio));
        }

        /// <summary>
        /// Transfers authored/relic art (author, title, and the backing
        /// TaleReference that produces the art description) from oldWeapon to
        /// newWeapon. No-op if either weapon lacks CompArt.
        ///
        /// Must be called BEFORE destroying oldWeapon: the TaleReference is moved,
        /// not cloned, so the old weapon's pointer is nulled here to stop
        /// CompArt.PostDestroy from calling TaleReference.ReferenceDestroyed() on
        /// the tale the new weapon now owns (which would decrement the tale's
        /// reference count and could free a tale still in use). Same
        /// before-destroy contract as <see cref="TransferRelicStatus"/>.
        /// </summary>
        public static void TransferArt(Thing oldWeapon, Thing newWeapon)
        {
            CompArt oldArt = oldWeapon.TryGetComp<CompArt>();
            CompArt newArt = newWeapon.TryGetComp<CompArt>();
            if (oldArt == null || newArt == null)
                return;

            // Drift already logged at startup; bail rather than half-transfer.
            if (ArtAuthorField == null || ArtTitleField == null || ArtTaleRefField == null)
                return;

            // Defensive: if the new weapon somehow already holds a generated
            // taleRef, release it before overwriting so its reference count isn't
            // stranded. (ConvertWeaponDef passes a null art source to SetQuality
            // precisely so this stays null, but don't depend on that here.)
            if (ArtTaleRefField.GetValue(newArt) is TaleReference staleNew)
                staleNew.ReferenceDestroyed();

            ArtAuthorField.SetValue(newArt, ArtAuthorField.GetValue(oldArt));
            ArtTitleField.SetValue(newArt, ArtTitleField.GetValue(oldArt));
            ArtTaleRefField.SetValue(newArt, ArtTaleRefField.GetValue(oldArt));

            // Hand off ownership of the TaleReference: clear it on the old weapon
            // so its impending Destroy() doesn't tear down the shared tale.
            ArtTaleRefField.SetValue(oldArt, null);
        }

        /// <summary>
        /// Transfers Ideology relic status from the old weapon to the new weapon.
        /// Must be called BEFORE destroying the old weapon — clears the old weapon's
        /// StyleSourcePrecept so that Thing.Destroy() does not fire Notify_ThingLost,
        /// which would trigger RelicDestroyed events, mood debuffs, and permanently
        /// orphan the relic precept.
        ///
        /// Updates both sides of the bidirectional reference:
        ///   Thing.StyleSourcePrecept → Precept_Relic (via CompStyleable)
        ///   Precept_Relic.generatedRelic → Thing (via reflection)
        ///
        /// No-op if Ideology is not active or the weapon is not a relic.
        /// </summary>
        public static void TransferRelicStatus(Thing oldWeapon, Thing newWeapon)
        {
            if (!ModsConfig.IdeologyActive)
                return;

            Precept_ThingStyle precept = oldWeapon.StyleSourcePrecept;
            if (precept == null)
                return;

            // Clear from old weapon BEFORE it gets destroyed to prevent
            // Precept_Relic.Notify_ThingLost from firing RelicDestroyed/RelicLost events.
            oldWeapon.StyleSourcePrecept = null;

            // Pre-seed the new weapon's CompStyleable state from the old weapon
            // before the StyleSourcePrecept setter runs below.
            //
            // styleDef: the SourcePrecept setter only writes styleDef when the
            // new def's randomStyleChance is 0 AND the ideo has a style mapping
            // for the new def. Either condition failing leaves the new weapon
            // styleless after conversion. Copying first preserves the visual
            // continuity; the setter still overwrites our copy when it has a
            // valid ideo-driven answer for the new def.
            //
            // everSeenByPlayer: the setter never touches this, so the copy is
            // strictly additive. Direct field assignment rather than
            // SetEverSeenByPlayer so we don't re-fire Notify_RelicSeenByPlayer
            // (which would spam the "relics collected" letter on every
            // customization of an already-seen relic).
            if (oldWeapon is ThingWithComps oldTwc && newWeapon is ThingWithComps newTwc
                && oldTwc.compStyleable != null && newTwc.compStyleable != null)
            {
                newTwc.compStyleable.styleDef = oldTwc.compStyleable.styleDef;
                newTwc.compStyleable.everSeenByPlayer = oldTwc.compStyleable.everSeenByPlayer;
            }

            // Point the new weapon back at the precept. The setter may overwrite
            // the styleDef we just copied via ideo.style.StyleForThingDef for
            // the new def — that's the right behavior when the lookup succeeds.
            newWeapon.StyleSourcePrecept = precept;

            // Update the Precept_Relic's private generatedRelic field to point
            // at the new weapon instance, keeping the precept→thing reference valid.
            if (GeneratedRelicField != null && precept is Precept_Relic)
            {
                GeneratedRelicField.SetValue(precept, newWeapon);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Optional integration with Vanilla Expanded Framework's trait-driven
    /// weapon-graphic override (<c>VEF.Weapons.CompApplyWeaponTraits</c>, the
    /// system Alpha Armoury's "attachable" traits ride on). VEF lets a
    /// <c>WeaponTraitDef</c> carry a <c>WeaponTraitDefExtension</c> that swaps the
    /// weapon's whole graphic for a given base def; its comp resolves the
    /// highest-priority matching override and writes the result straight into
    /// <c>Thing.graphicInt</c> — the backing field <c>Thing.Graphic</c> returns —
    /// with no Harmony patch on the getter. So <em>once that comp has run</em>, the
    /// override is reachable through <c>Thing.Graphic</c> like any vanilla graphic.
    ///
    /// <para>The catch is <em>when</em> it runs: VEF only recomputes on equip and
    /// on load (and its trait scan is memoized in a private cache that our trait
    /// edits don't invalidate). Neither fires during our preview, and neither fires
    /// for a weapon that stays put while we mutate its trait list.
    /// <see cref="RefreshTraitGraphic"/> reproduces VEF's own guarded refresh —
    /// flush the cache, then re-run the override iff a trait still carries an
    /// extension — so the prospective preview and the post-edit real weapon both
    /// show what an equip would.</para>
    ///
    /// <para>All access goes through reflection so this mod compiles and runs
    /// without VEF installed. Mirrors the structure of
    /// <see cref="AlphaArmouryIntegration"/> and
    /// <see cref="VEFRecipeInheritanceIntegration"/>: the static ctor resolves the
    /// type/members once and logs a single warning when VEF is loaded but the
    /// integration surface has drifted (renamed type/method/field). When VEF is
    /// absent the integration is silently unavailable and every entry point no-ops,
    /// leaving vanilla graphic resolution untouched.</para>
    ///
    /// <para>The static ctor is forced to run at startup (ModInitializer reads
    /// <see cref="Available"/>) rather than lazily on first customization: drift is
    /// determinable from loaded assemblies alone, so it should surface during load.
    /// Unlike <see cref="VEFRecipeInheritanceIntegration"/> — touched at load by
    /// <c>WorkbenchUtility.Initialize</c> — this integration has no other startup
    /// consumer, so the probe is its only early trigger.</para>
    /// </summary>
    internal static class VEFWeaponTraitGraphicsIntegration
    {
        private const string CompTypeName = "VEF.Weapons.CompApplyWeaponTraits";
        private const string DeleteCachesMethodName = "DeleteCaches";
        private const string ChangeGraphicMethodName = "ChangeGraphic";
        private const string GetDetailsMethodName = "GetDetails";
        private const string GraphicIntFieldName = "graphicInt";

        // VEF.Weapons.CompApplyWeaponTraits — the comp VEF injects into every
        // ThingDef with CompProperties_UniqueWeapon at startup. Null when VEF/Alpha
        // Armoury isn't in the load order, the normal soft-dependency case.
        private static readonly Type CompType;

        // DeleteCaches: drops contentDetails (VEF's memoized per-weapon trait-extension
        // scan) plus the comp's other caches. Our Add/RemoveTrait and the preview's
        // direct list swap mutate the trait list without VEF's knowledge, so this must
        // run before ChangeGraphic, or it resolves against the pre-edit trait set.
        private static readonly MethodInfo DeleteCachesMethod;

        // ChangeGraphic: resolves the highest-priority override for the parent's def
        // (or the def's own graphicData when none matches) and writes it to graphicInt.
        private static readonly MethodInfo ChangeGraphicMethod;

        // GetDetails: the List<WeaponTraitDefExtension> for the parent's current
        // traits. VEF guards ChangeGraphic behind "details non-empty"; we mirror that.
        private static readonly MethodInfo GetDetailsMethod;

        // Thing.graphicInt — the lazily-resolved cached graphic behind Thing.Graphic.
        // Nulled to revert to vanilla resolution when no trait override applies,
        // including clearing a stale override left by VEF after the last extension
        // trait was removed.
        private static readonly FieldInfo GraphicIntField;

        public static bool Available =>
            CompType != null
            && DeleteCachesMethod != null
            && ChangeGraphicMethod != null
            && GetDetailsMethod != null
            && GraphicIntField != null;

        private static bool runtimeFailureLogged;

        static VEFWeaponTraitGraphicsIntegration()
        {
            try
            {
                CompType = GenTypes.GetTypeInAnyAssembly(CompTypeName);
                if (CompType != null)
                {
                    // Resolve the exact parameterless overloads (Type.EmptyTypes) so a
                    // future VEF overload can't ambiguate the lookup.
                    DeleteCachesMethod = CompType.GetMethod(DeleteCachesMethodName,
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    ChangeGraphicMethod = CompType.GetMethod(ChangeGraphicMethodName,
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    GetDetailsMethod = CompType.GetMethod(GetDetailsMethodName,
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    GraphicIntField = typeof(Thing).GetField(GraphicIntFieldName,
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Unique Weapons Unbound] VEF weapon-trait-graphics reflection failed: " + ex);
                return;
            }

            // VEF is "loaded" iff the comp type resolved. Warn only when it's present
            // but a member drifted, so users without VEF stay quiet.
            if (CompType != null && !Available)
            {
                Log.Warning("[Unique Weapons Unbound] VEF active but its weapon-trait graphic API ("
                    + CompTypeName + " " + DeleteCachesMethodName + "/" + ChangeGraphicMethodName + "/"
                    + GetDetailsMethodName + ", or Thing." + GraphicIntFieldName
                    + ") could not be resolved; trait-driven weapon graphics won't refresh after "
                    + "customization until the weapon is next equipped. VEF API may have changed.");
            }
        }

        /// <summary>
        /// Re-runs VEF's trait-driven graphic override against the weapon's current
        /// trait list, exactly as an equip would, so the resolved graphic lands in
        /// <c>graphicInt</c> for the next <c>Thing.Graphic</c> read / draw.
        ///
        /// <para>Used in two places: the dialog preview calls it on the prospective
        /// Thing before reading its <c>Graphic</c>, and the customization job's
        /// finalize toil calls it on the real weapon once the trait list is final
        /// (VEF would otherwise not catch the change until a later equip/drop).</para>
        ///
        /// <para>Flushes VEF's memoized trait cache first (our edits bypass it), then
        /// mirrors VEF's own guard: invoke <c>ChangeGraphic</c> only when a trait
        /// still carries a <c>WeaponTraitDefExtension</c>. With none, null
        /// <c>graphicInt</c> so the appearance reverts to vanilla resolution — this
        /// is what clears a stale override after the last attachment trait is
        /// removed. No-op when VEF is absent, the weapon lacks the comp, or the
        /// weapon is null. Self-contained: any throw from VEF's recompute is logged
        /// once and falls back to vanilla resolution rather than propagating into
        /// GUI layout or the job toil.</para>
        /// </summary>
        public static void RefreshTraitGraphic(Thing weapon)
        {
            if (!Available || weapon == null)
                return;

            ThingComp comp = FindComp(weapon);
            if (comp == null)
                return;

            try
            {
                DeleteCachesMethod.Invoke(comp, null);

                if (GetDetailsMethod.Invoke(comp, null) is ICollection details && details.Count > 0)
                    ChangeGraphicMethod.Invoke(comp, null);
                else
                    GraphicIntField.SetValue(weapon, null);
            }
            catch (Exception ex)
            {
                // A malformed override def (or a VEF internal change) threw mid-
                // recompute. Log once, then clear graphicInt so we never leave a
                // half-applied override — the weapon falls back to its vanilla
                // graphic. Setting a reference field to null on a Thing can't throw,
                // so the fallback is safe to run inside the catch.
                if (!runtimeFailureLogged)
                {
                    runtimeFailureLogged = true;
                    Log.Error("[Unique Weapons Unbound] VEF trait-graphic refresh failed: " + ex);
                }
                GraphicIntField.SetValue(weapon, null);
            }
        }

        // VEF's comp type isn't known at compile time, so TryGetComp<T> is out —
        // scan AllComps for an instance (IsInstanceOfType also catches any subclass).
        private static ThingComp FindComp(Thing weapon)
        {
            if (!(weapon is ThingWithComps twc))
                return null;

            List<ThingComp> comps = twc.AllComps;
            if (comps == null)
                return null;

            for (int i = 0; i < comps.Count; i++)
            {
                if (CompType.IsInstanceOfType(comps[i]))
                    return comps[i];
            }
            return null;
        }
    }
}

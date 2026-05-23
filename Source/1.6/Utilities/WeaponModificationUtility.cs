using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Mutates a weapon Thing in place: adds/removes traits (delegating
    /// ability-comp wiring to <see cref="EquippableAbilityUtility"/>), sets
    /// cosmetic properties (name, color, texture), and spawns refunded
    /// resources. Def conversion (base↔unique) lives in
    /// <see cref="WeaponDefConversion"/>; ingredient gathering and reservation
    /// for a customization job lives in
    /// <see cref="HaulPlanning.IngredientReservation"/>.
    /// </summary>
    public static class WeaponModificationUtility
    {
        // Reflection fields for CompUniqueWeapon private members. Resolved once
        // at static-init; verified by Initialize() so a RimWorld API rename
        // surfaces as a startup error instead of a silent no-op on every
        // SetName/SetColor/AddTrait/RemoveTrait at runtime.
        internal static readonly FieldInfo CompNameField = typeof(CompUniqueWeapon)
            .GetField("name", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static readonly FieldInfo CompColorField = typeof(CompUniqueWeapon)
            .GetField("color", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static readonly FieldInfo IgnoreAccuracyField = typeof(CompUniqueWeapon)
            .GetField("ignoreAccuracyMaluses", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Verifies that every cached CompUniqueWeapon FieldInfo resolved.
        /// Should be called during StaticConstructorOnStartup so a RimWorld
        /// API rename surfaces as a startup error rather than a silent no-op
        /// on every later SetName/SetColor/AddTrait/RemoveTrait. Pure
        /// diagnostic — no state is built here; the FieldInfos are resolved
        /// at class-load time by the field initializers above.
        /// </summary>
        public static void VerifyReflection()
        {
            if (CompNameField == null)
                Log.Error("[Unique Weapons Unbound] CompUniqueWeapon.name field not found via reflection; "
                    + "weapon renaming will silently no-op. RimWorld API may have changed.");
            if (CompColorField == null)
                Log.Error("[Unique Weapons Unbound] CompUniqueWeapon.color field not found via reflection; "
                    + "weapon color changes will silently no-op. RimWorld API may have changed.");
            if (IgnoreAccuracyField == null)
                Log.Error("[Unique Weapons Unbound] CompUniqueWeapon.ignoreAccuracyMaluses field not found via reflection; "
                    + "accuracy-malus cache won't be invalidated after trait changes. RimWorld API may have changed.");
        }

        public static void AddTrait(Thing weapon, WeaponTraitDef trait)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
            {
                Log.Error("[Unique Weapons Unbound] AddTrait: weapon has no CompUniqueWeapon.");
                return;
            }

            comp.AddTrait(trait);

            // Mirror RemoveTrait: invalidate the cached ignoreAccuracyMaluses so a
            // newly-added accuracy-malus-immunity trait actually takes effect on the
            // next shot. Vanilla Setup() never resets this cache, so without this
            // the weapon's accuracy behaviour reflects the trait list at first read
            // until the comp is recreated (def conversion or save/load).
            if (IgnoreAccuracyField != null)
                IgnoreAccuracyField.SetValue(comp, null);

            // Mirror RemoveTrait: if any earlier customization, auto-gen, or
            // mod-side handling left a stale Ability cached on the equippable-
            // ability comp, vanilla Setup() won't rebuild it — it only reassigns
            // props, and the lazy AbilityForReading getter keeps returning the
            // cached instance. Reset so the getter constructs a fresh Ability
            // matching the new abilityProps. Two ability traits rarely coexist
            // thanks to exclusionTags <li>Ability</li>, but the stale-cache state
            // can still be reached via a partially-healed save or non-vanilla
            // trait wiring.
            if (trait.abilityProps != null)
                EquippableAbilityUtility.ResetState(weapon);

            EquippableAbilityUtility.SetupAndPreserveCharges(weapon, comp);
        }

        public static void RemoveTrait(Thing weapon, WeaponTraitDef trait)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
            {
                Log.Error("[Unique Weapons Unbound] RemoveTrait: weapon has no CompUniqueWeapon.");
                return;
            }

            comp.TraitsListForReading.Remove(trait);

            // Invalidate the cached ignoreAccuracyMaluses so it recalculates on next access
            if (IgnoreAccuracyField != null)
                IgnoreAccuracyField.SetValue(comp, null);

            // Vanilla CompUniqueWeapon.Setup() only assigns props for traits that
            // have abilityProps; it never clears them when an ability-granting trait
            // is removed, and never resets the cached Ability instance. Without this
            // scrub, an equipped pawn keeps the removed trait's gizmo (e.g. the
            // grenade launcher's launch+reload command) the next time the weapon is
            // re-equipped — even if the trait was swapped for one with different
            // abilityProps, because the lazy AbilityForReading getter only rebuilds
            // when its cache is null.
            if (trait.abilityProps != null)
                EquippableAbilityUtility.ResetState(weapon);

            EquippableAbilityUtility.SetupAndPreserveCharges(weapon, comp);
        }

        /// <summary>
        /// Scrubs the random state left behind by <c>CompUniqueWeapon.PostPostMake</c>
        /// on a freshly minted unique weapon: the auto-rolled trait list, the
        /// generated name and color, the lazy <c>ignoreAccuracyMaluses</c> cache,
        /// and the equippable-ability comp's stale <c>props</c> + cached
        /// <see cref="Ability"/> instance. Called by <see cref="WeaponDefConversion"/>
        /// right after <c>ThingMaker.MakeThing</c> on a unique-weapon def so the
        /// customization pipeline starts from a clean slate.
        ///
        /// Without the ability-comp scrub, an ability trait randomly rolled by
        /// <c>InitializeTraits</c> (e.g. SmokeLauncher → LaunchSmokeShell) wires
        /// up <c>CompEquippableAbilityReloadable.props</c> and lazily constructs
        /// its <c>Ability</c>. Clearing the trait list afterwards doesn't undo
        /// that wiring — vanilla <c>Setup()</c> only assigns props for traits
        /// currently in the list, never clears them — and the cached Ability is
        /// even deep-scribed across save/load, so the phantom gizmo persists.
        ///
        /// No-op on non-unique target defs (no CompUniqueWeapon).
        /// </summary>
        internal static void ClearAutoGeneratedUniqueState(Thing weapon)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
                return;

            comp.TraitsListForReading.Clear();
            CompNameField?.SetValue(comp, null);
            CompColorField?.SetValue(comp, null);
            IgnoreAccuracyField?.SetValue(comp, null);
            EquippableAbilityUtility.ResetState(weapon);
        }

        public static void SetName(Thing weapon, string name)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp != null && CompNameField != null)
                CompNameField.SetValue(comp, name);

            // Keep CompArt.Title in sync for inspection dialogs
            CompArt artComp = weapon.TryGetComp<CompArt>();
            if (artComp != null && !string.IsNullOrEmpty(name))
                artComp.Title = name;
        }

        public static void SetColor(Thing weapon, ColorDef color)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp != null && CompColorField != null)
            {
                CompColorField.SetValue(comp, color);
                weapon.Notify_ColorChanged();
            }
        }

        public static void SetTextureIndex(Thing weapon, int index)
        {
            weapon.overrideGraphicIndex = index;
        }

        /// <summary>
        /// Spawns resources near a position (e.g. the workbench).
        /// Used to refund resources when removing traits.
        /// </summary>
        public static void SpawnResourcesNear(
            Map map, IntVec3 center, List<ThingDefCountClass> resources)
        {
            if (resources == null || resources.Count == 0)
                return;

            foreach (ThingDefCountClass resource in resources)
            {
                if (resource.count <= 0)
                    continue;

                Thing thing = ThingMaker.MakeThing(resource.thingDef);
                thing.stackCount = resource.count;
                GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near);
            }
        }

    }
}

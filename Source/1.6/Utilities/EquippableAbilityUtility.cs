using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Manages the equippable-ability comp
    /// (<see cref="CompEquippableAbility"/> / <see cref="CompEquippableAbilityReloadable"/>)
    /// on unique weapons during the customization pipeline. Concentrates the
    /// reflection and vanilla-quirk handling so the rest of the codebase
    /// doesn't have to know that:
    ///   - <c>CompUniqueWeapon.Setup()</c> only assigns <c>props</c> for traits
    ///     that have <c>abilityProps</c>; it never clears stale entries when an
    ///     ability trait is removed and never resets the lazy-cached Ability.
    ///   - <c>Notify_PropsChanged()</c> refills charges on every call, turning
    ///     unrelated customizations into free reloads.
    ///   - The cached <c>Ability</c> instance is deep-scribed, so phantom
    ///     wirings survive save/reload until explicitly scrubbed.
    /// All entry points are idempotent and safe on null/destroyed/non-unique
    /// weapons.
    /// </summary>
    public static class EquippableAbilityUtility
    {
        // CompEquippableAbility caches its constructed Ability in this private
        // field and ScribeDeeps it across save/load. CompUniqueWeapon.Setup()
        // never clears it, so removing or swapping an ability-granting trait
        // leaves the equipping pawn with the stale gizmo unless we scrub it
        // ourselves. Used by ResetState (write), SetupAndPreserveCharges (read
        // snapshot for the ReferenceEquals charge round-trip), and
        // HealOrphaned (read for orphan detection).
        internal static readonly FieldInfo CachedAbilityField =
            typeof(CompEquippableAbility)
                .GetField("ability", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Verifies that the cached CompEquippableAbility FieldInfo resolved.
        /// Mirrors <see cref="WeaponModificationUtility.VerifyReflection"/>: if a
        /// RimWorld API rename drops the field, the heal-orphaned-cache check and
        /// the preserve-charges restore both silently degrade to no-ops, so surface
        /// the breakage as a startup error instead.
        /// </summary>
        public static void VerifyReflection()
        {
            if (CachedAbilityField == null)
                Log.Error("[Unique Weapons Unbound] CompEquippableAbility.ability field not found via reflection; "
                    + "orphan-ability heal and charge preservation will silently no-op. RimWorld API may have changed.");
        }

        /// <summary>
        /// Entry for the customization JobDriver's finalize toil. Runs the heal
        /// check first (so a stale instance never collides with a legitimate
        /// re-wire) then defers to <see cref="SetupAndPreserveCharges"/> for
        /// the actual prop wiring. The finalize toil also fires for cosmetics-
        /// only customizations that never enter Add/RemoveTrait, so this entry
        /// must preserve charges — vanilla Setup unconditionally refills them.
        /// </summary>
        public static void SyncToTraits(Thing weapon)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
                return;
            TryHealOrphanedCache(weapon, comp);
            SetupAndPreserveCharges(weapon, comp);
        }

        /// <summary>
        /// Wrapper around <c>CompUniqueWeapon.Setup(false)</c> that preserves
        /// the equipped ability's <c>RemainingCharges</c> across the call.
        /// Vanilla Setup walks the trait list and, for every ability trait,
        /// calls <c>CompEquippableAbilityReloadable.Notify_PropsChanged()</c>,
        /// which forces <c>RemainingCharges = MaxCharges</c>. That's correct on
        /// PostPostMake and save load, but it also fires on every customization
        /// op — turning every dialog confirm into a free reload of any
        /// unchanged ability trait (skipping the steel/chemfuel/bioferrite
        /// Reload job). We snapshot the charges before, then restore them only
        /// if the same Ability instance survived; a different instance means
        /// the ability trait was added or swapped this op (player paid for
        /// the new trait), in which case fresh max charges is the right
        /// outcome.
        ///
        /// No-op for cooldown-only abilities (e.g. EMPPulser): they leave
        /// <c>maxCharges = 0</c>, so <c>UsesCharges</c> is false, the cooldown
        /// lives on <c>Ability.cooldownEndTick</c> (which Notify_PropsChanged
        /// doesn't touch), and the snapshot/restore round-trips zero.
        /// </summary>
        public static void SetupAndPreserveCharges(Thing weapon, CompUniqueWeapon comp)
        {
            CompEquippableAbilityReloadable abilityComp =
                weapon.TryGetComp<CompEquippableAbilityReloadable>();

            Ability priorAbility = null;
            int priorCharges = 0;
            if (abilityComp != null && CachedAbilityField != null)
            {
                priorAbility = (Ability)CachedAbilityField.GetValue(abilityComp);
                if (priorAbility != null)
                    priorCharges = priorAbility.RemainingCharges;
            }

            comp.Setup(false);

            if (priorAbility != null && abilityComp != null && CachedAbilityField != null)
            {
                Ability currentAbility = (Ability)CachedAbilityField.GetValue(abilityComp);
                if (ReferenceEquals(currentAbility, priorAbility))
                    abilityComp.RemainingCharges = priorCharges;
            }
        }

        /// <summary>
        /// Restores CompEquippableAbilityReloadable to its def-default state:
        /// drops the cached Ability and points <c>props</c> back at the empty
        /// stub from <c>weapon.def.comps</c>. A subsequent AddTrait →
        /// <c>CompUniqueWeapon.Setup()</c> will re-assign <c>props</c> from the
        /// new trait's <c>abilityProps</c> and the lazy
        /// <c>AbilityForReading</c> getter will construct a fresh Ability
        /// from it.
        ///
        /// Called from trait remove/add for ability traits (so the cache
        /// matches the post-change trait list) and from
        /// <see cref="WeaponModificationUtility.ClearAutoGeneratedUniqueState"/>
        /// after a base→unique conversion clears the auto-rolled trait list.
        /// </summary>
        public static void ResetState(Thing weapon)
        {
            CompEquippableAbilityReloadable abilityComp =
                weapon.TryGetComp<CompEquippableAbilityReloadable>();
            if (abilityComp == null)
                return;

            CachedAbilityField?.SetValue(abilityComp, null);

            foreach (CompProperties cp in weapon.def.comps)
            {
                if (cp is CompProperties_EquippableAbilityReloadable defaultProps)
                {
                    abilityComp.props = defaultProps;
                    break;
                }
            }
        }

        /// <summary>
        /// Heal entry intended for the customization JobDriver's
        /// <c>Notify_Starting</c> override — the earliest JobDriver-lifecycle
        /// hook, fired before any toil runs. Detects an orphaned cached
        /// <see cref="Ability"/> (a deep-scribed phantom such as
        /// LaunchSmokeShell, left on a weapon by a pre-fix base→unique
        /// conversion) and scrubs it.
        ///
        /// Triggering at job start means the player just has to initiate
        /// customization on an affected weapon — they don't need to confirm
        /// changes, sit through the haul/work loops, or even let the pawn
        /// reach the bench. Any subsequent interruption (cancel, draft,
        /// bench loss, weapon destruction) leaves the weapon already healed.
        ///
        /// Idempotent and safe on null/destroyed weapons, non-unique weapons,
        /// and weapons without ability comps. If a pawn is currently holding
        /// the weapon as equipment, the pawn's ability tracker is refreshed
        /// so the scrub reflects in the gizmo bar immediately — the standard
        /// equip/unequip events don't fire on the bare scrub path.
        /// </summary>
        public static void HealOrphaned(Thing weapon)
        {
            if (weapon == null || weapon.Destroyed)
                return;
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
                return;
            if (!TryHealOrphanedCache(weapon, comp))
                return;

            // The cache scrub bypasses the equip/unequip flow that would
            // normally invalidate Pawn_AbilityTracker.allAbilitiesCached, so
            // a still-equipped phantom would linger as a gizmo until the next
            // refresh trigger. Force one when we know the weapon is held.
            if (weapon is ThingWithComps twc
                && twc.ParentHolder is Pawn_EquipmentTracker tracker
                && tracker.pawn != null)
            {
                tracker.pawn.abilities?.Notify_TemporaryAbilitiesChanged();
            }
        }

        /// <summary>
        /// Resets the equippable-ability comp when its cached
        /// <see cref="Ability"/> has no backing trait in the current list — the
        /// leftover from a pre-fix base→unique conversion that wired up an
        /// auto-rolled ability trait before clearing the trait list. Returns
        /// true when a scrub was performed.
        /// </summary>
        private static bool TryHealOrphanedCache(Thing weapon, CompUniqueWeapon comp)
        {
            CompEquippableAbility abilityComp = weapon.TryGetComp<CompEquippableAbility>();
            if (abilityComp == null || CachedAbilityField == null)
                return false;

            Ability cached = (Ability)CachedAbilityField.GetValue(abilityComp);
            if (cached?.def == null)
                return false;

            foreach (WeaponTraitDef trait in comp.TraitsListForReading)
            {
                if (trait.abilityProps?.abilityDef == cached.def)
                    return false;
            }

            ResetState(weapon);
            return true;
        }
    }
}

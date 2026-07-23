using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Manages the equippable-ability comp (CompEquippableAbility /
    // CompEquippableAbilityReloadable) on unique weapons during the
    // customization pipeline. Concentrates the reflection and vanilla-quirk
    // handling so the rest of the codebase doesn't have to know that:
    //   - CompUniqueWeapon.Setup() only assigns props for traits that have
    //     abilityProps; it never clears stale entries when an ability trait is
    //     removed and never resets the lazy-cached Ability.
    //   - Notify_PropsChanged() refills charges on every call, turning
    //     unrelated customizations into free reloads.
    //   - The cached Ability instance is deep-scribed, so phantom wirings
    //     survive save/reload until explicitly scrubbed.
    // All entry points are idempotent and safe on null/destroyed/non-unique
    // weapons.
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

        // Verifies that the cached CompEquippableAbility FieldInfo resolved.
        // Mirrors WeaponModificationUtility.VerifyReflection: if a RimWorld API
        // rename drops the field, the heal-orphaned-cache check and the
        // preserve-charges restore both silently degrade to no-ops, so surface
        // the breakage as a startup error instead.
        public static void VerifyReflection()
        {
            if (CachedAbilityField == null)
                Log.Error("[Unique Weapons Unbound] CompEquippableAbility.ability field not found via reflection; "
                    + "orphan-ability heal and charge preservation will silently no-op. RimWorld API may have changed.");
        }

        // Entry for the customization JobDriver's finalize toil. Runs the heal
        // check first (so a stale instance never collides with a legitimate
        // re-wire) then defers to SetupAndPreserveCharges for the actual prop
        // wiring. The finalize toil also fires for cosmetics-only
        // customizations that never enter Add/RemoveTrait, so this entry must
        // preserve charges — vanilla Setup unconditionally refills them.
        public static void SyncToTraits(Thing weapon)
        {
            CompUniqueWeapon comp = weapon.TryGetComp<CompUniqueWeapon>();
            if (comp == null)
                return;
            TryHealOrphanedCache(weapon, comp);
            SetupAndPreserveCharges(weapon, comp);
        }

        // Wrapper around CompUniqueWeapon.Setup(false) that preserves the
        // equipped ability's RemainingCharges across the call. Vanilla Setup
        // walks the trait list and, for every ability trait, calls
        // CompEquippableAbilityReloadable.Notify_PropsChanged(), which forces
        // RemainingCharges = MaxCharges. That's correct on PostPostMake and
        // save load, but it also fires on every customization op — turning
        // every dialog confirm into a free reload of any unchanged ability
        // trait (skipping the steel/chemfuel/bioferrite Reload job). We
        // snapshot the charges before, then restore them only if the same
        // Ability instance survived; a different instance means the ability
        // trait was added or swapped this op (player paid for the new trait),
        // in which case fresh max charges is the right outcome.
        //
        // No-op for cooldown-only abilities (e.g. EMPPulser): they leave
        // maxCharges = 0, so UsesCharges is false, the cooldown lives on
        // Ability.cooldownEndTick (which Notify_PropsChanged doesn't touch),
        // and the snapshot/restore round-trips zero.
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

        // Restores CompEquippableAbilityReloadable to its def-default state:
        // drops the cached Ability and points props back at the empty stub from
        // weapon.def.comps. A subsequent AddTrait → CompUniqueWeapon.Setup()
        // will re-assign props from the new trait's abilityProps and the lazy
        // AbilityForReading getter will construct a fresh Ability from it.
        //
        // Called from trait remove/add for ability traits (so the cache matches
        // the post-change trait list) and from
        // WeaponModificationUtility.ClearAutoGeneratedUniqueState after a
        // base→unique conversion clears the auto-rolled trait list.
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

        // Heal entry intended for the customization JobDriver's Notify_Starting
        // override — the earliest JobDriver-lifecycle hook, fired before any
        // toil runs. Detects an orphaned cached Ability (a deep-scribed phantom
        // such as LaunchSmokeShell, left on a weapon by a pre-fix base→unique
        // conversion) and scrubs it.
        //
        // Triggering at job start means the player just has to initiate
        // customization on an affected weapon — they don't need to confirm
        // changes, sit through the haul/work loops, or even let the pawn reach
        // the bench. Any subsequent interruption (cancel, draft, bench loss,
        // weapon destruction) leaves the weapon already healed.
        //
        // Idempotent and safe on null/destroyed weapons, non-unique weapons,
        // and weapons without ability comps. If a pawn is currently holding the
        // weapon as equipment, the pawn's ability tracker is refreshed so the
        // scrub reflects in the gizmo bar immediately — the standard
        // equip/unequip events don't fire on the bare scrub path.
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

        // Resets the equippable-ability comp when its cached Ability has no
        // backing trait in the current list — the leftover from a pre-fix
        // base→unique conversion that wired up an auto-rolled ability trait
        // before clearing the trait list. Returns true when a scrub was
        // performed.
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

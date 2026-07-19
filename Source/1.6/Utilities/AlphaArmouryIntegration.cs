using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Optional integration with Alpha Armoury (packageId sarg.alphaarmoury).
    // Alpha Armoury's WeaponKit item stores a single WeaponTraitDef in a public
    // trait field; using the kit applies that trait to a compatible unique
    // weapon. For progression-mode trait visibility we treat kits as
    // player-discoverable sources alongside actual unique weapons.
    //
    // All access goes through reflection so this mod compiles and runs without
    // Alpha Armoury installed. The sibling kit defs (Converter / Remover /
    // TabulaRasa) don't carry a trait and are intentionally ignored — only the
    // WeaponKit class is recognised here. If Alpha Armoury is loaded but its
    // API has drifted (renamed type/field, unexpected field type), the static
    // ctor logs a warning. ModInitializer forces that resolution at startup (by
    // reading Available), so drift surfaces during load — drift is determinable
    // from loaded assemblies alone, with no game-state dependency, so it
    // shouldn't wait for first use. Kit traits only feed progression-mode trait
    // restriction, so the warning says as much; but a player who enables that
    // setting in a later session still wants to know the integration is broken
    // before relying on it, which is why the warning no longer gates on it.
    internal static class AlphaArmouryIntegration
    {
        private const string PackageId = "sarg.alphaarmoury";
        private const string WeaponKitTypeName = "AlphaArmoury.WeaponKit";
        private const string TraitFieldName = "trait";

        private static readonly Type WeaponKitType;
        private static readonly FieldInfo TraitField;

        public static bool Available => TraitField != null;

        private static bool runtimeFailureLogged;

        static AlphaArmouryIntegration()
        {
            try
            {
                WeaponKitType = GenTypes.GetTypeInAnyAssembly(WeaponKitTypeName);
                if (WeaponKitType != null)
                {
                    TraitField = WeaponKitType.GetField(
                        TraitFieldName, BindingFlags.Public | BindingFlags.Instance);
                    if (TraitField != null
                        && !typeof(WeaponTraitDef).IsAssignableFrom(TraitField.FieldType))
                    {
                        TraitField = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Unique Weapons Unbound] Alpha Armoury reflection failed (kit traits "
                    + "will be ignored; only affects progression-mode trait restriction): " + ex);
                return;
            }

            // AA is present iff its packageId is active — a renamed top-level type
            // would null WeaponKitType, so the packageId (not the type) is what
            // proves AA is the cause. Warn whenever AA is active but the kit surface
            // didn't resolve, regardless of the progression setting: the note tells
            // unaffected players they can ignore it, while a player who enables
            // progression later still learns the integration is broken.
            if (!Available && ModsConfig.IsActive(PackageId))
            {
                Log.Warning("[Unique Weapons Unbound] Alpha Armoury active but "
                    + WeaponKitTypeName + "." + TraitFieldName
                    + " could not be resolved as WeaponTraitDef; kit traits will be ignored. "
                    + "This only affects you if progression-mode trait restriction is enabled.");
            }
        }

        // Returns true and emits the stored trait if thing is an Alpha Armoury
        // weapon kit carrying a non-null trait. Returns false for any non-kit
        // thing, kits with a null trait, or when the integration is
        // unavailable.
        public static bool TryGetKitTrait(Thing thing, out WeaponTraitDef trait)
        {
            trait = null;
            if (!Available || thing == null || !WeaponKitType.IsInstanceOfType(thing))
                return false;

            try
            {
                trait = TraitField.GetValue(thing) as WeaponTraitDef;
            }
            catch (Exception ex)
            {
                // Defensive: IsInstanceOfType + Public|Instance reflection shouldn't
                // raise on a well-typed kit. If it does, log once and silently treat
                // as non-kit thereafter so we don't spam the log every frame.
                if (!runtimeFailureLogged)
                {
                    runtimeFailureLogged = true;
                    Log.Error("[Unique Weapons Unbound] Alpha Armoury kit read failed: " + ex);
                }
                return false;
            }
            return trait != null;
        }
    }
}

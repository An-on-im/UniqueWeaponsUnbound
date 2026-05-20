using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Optional integration with Alpha Armoury (packageId sarg.alphaarmoury).
    /// Alpha Armoury's <c>WeaponKit</c> item stores a single <see cref="WeaponTraitDef"/>
    /// in a public <c>trait</c> field; using the kit applies that trait to a compatible
    /// unique weapon. For progression-mode trait visibility we treat kits as
    /// player-discoverable sources alongside actual unique weapons.
    ///
    /// All access goes through reflection so this mod compiles and runs without
    /// Alpha Armoury installed. The sibling kit defs (Converter / Remover / TabulaRasa)
    /// don't carry a trait and are intentionally ignored — only the <c>WeaponKit</c>
    /// class is recognised here. If Alpha Armoury is loaded but its API has drifted
    /// (renamed type/field, unexpected field type), the static ctor logs a warning
    /// — but only when progression-mode trait restriction is enabled, since kits
    /// don't contribute anywhere else. Progression has long-term gameplay impact,
    /// so an affected player will see the warning on later sessions even if the
    /// setting flipped on after this one.
    /// </summary>
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
                if (ProgressionModeActive)
                    Log.Warning("[Unique Weapons Unbound] Alpha Armoury reflection failed: " + ex);
                return;
            }

            if (!Available && ModsConfig.IsActive(PackageId) && ProgressionModeActive)
            {
                Log.Warning("[Unique Weapons Unbound] Alpha Armoury active but "
                    + WeaponKitTypeName + "." + TraitFieldName
                    + " could not be resolved as WeaponTraitDef; kit traits will be ignored.");
            }
        }

        // Kit traits only feed the progression pool, so a broken integration is
        // a graceful no-op for players who never opt in. Gating the startup
        // diagnostic on this setting keeps the log clean for the majority case.
        private static bool ProgressionModeActive =>
            UWU_Mod.Settings?.restrictTraitsToDiscovered == true;

        /// <summary>
        /// Returns true and emits the stored trait if <paramref name="thing"/> is an
        /// Alpha Armoury weapon kit carrying a non-null trait. Returns false for any
        /// non-kit thing, kits with a null trait, or when the integration is unavailable.
        /// </summary>
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

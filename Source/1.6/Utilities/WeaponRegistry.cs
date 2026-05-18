using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    /// <summary>
    /// Caches base↔unique weapon pair mappings at startup and provides
    /// runtime lookups for weapon pair resolution.
    /// </summary>
    public static class WeaponRegistry
    {
        private static Dictionary<ThingDef, ThingDef> baseToUnique;
        private static Dictionary<ThingDef, ThingDef> uniqueToBase;

        /// <summary>
        /// Builds the base↔unique weapon pair cache. Must be called during
        /// StaticConstructorOnStartup (after all defs are loaded). A non-null
        /// <paramref name="report"/> absorbs any fatal exception so the rest
        /// of the mod can still initialize; passing null preserves the
        /// throwing contract for direct callers.
        /// </summary>
        public static void Initialize(InitDiagnostics report = null)
        {
            try
            {
                baseToUnique = new Dictionary<ThingDef, ThingDef>();
                uniqueToBase = new Dictionary<ThingDef, ThingDef>();

                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
                {
                    try
                    {
                        RegisterUniqueWeaponDef(def);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[Unique Weapons Unbound] Skipped weapon registration for "
                            + def.SourceForLog() + " due to error: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (report == null) throw;
                report.RecordFailure(nameof(WeaponRegistry), ex);
            }
        }

        private static void RegisterUniqueWeaponDef(ThingDef def)
        {
            if (!def.HasComp(typeof(CompUniqueWeapon)))
                return;

            ThingDef baseDef = FindBaseWeapon(def);
            if (baseDef != null)
            {
                uniqueToBase[def] = baseDef;
                baseToUnique[baseDef] = def;
            }
        }

        /// <summary>
        /// Detects the base weapon for a unique weapon def.
        /// Primary: descriptionHyperlinks. Fallback: naming convention.
        /// </summary>
        private static ThingDef FindBaseWeapon(ThingDef uniqueDef)
        {
            // Primary: descriptionHyperlinks — works for modded weapons that may not follow naming conventions
            if (uniqueDef.descriptionHyperlinks != null)
            {
                foreach (DefHyperlink link in uniqueDef.descriptionHyperlinks)
                {
                    if (link.def is ThingDef linked && linked.IsWeapon && !linked.HasComp(typeof(CompUniqueWeapon)))
                        return linked;
                }
            }

            // Fallback: naming convention ({BaseDefName}_Unique)
            if (uniqueDef.defName.EndsWith("_Unique"))
            {
                string baseName = uniqueDef.defName.Substring(0, uniqueDef.defName.Length - "_Unique".Length);
                return DefDatabase<ThingDef>.GetNamedSilentFail(baseName);
            }

            return null;
        }

        /// <summary>
        /// Returns the unique variant for a base weapon def, or null if none exists.
        /// </summary>
        public static ThingDef GetUniqueVariant(ThingDef baseDef)
        {
            return baseToUnique.TryGetValue(baseDef, out ThingDef unique) ? unique : null;
        }

        /// <summary>
        /// All registered unique-variant ThingDefs (one per base↔unique pair).
        /// Used by the startup diagnostic to bucket pairs by source mod.
        /// </summary>
        public static IEnumerable<ThingDef> AllUniqueDefs => uniqueToBase.Keys;

        /// <summary>
        /// Returns the base weapon for a unique weapon def, or null if not found.
        /// </summary>
        public static ThingDef GetBaseVariant(ThingDef uniqueDef)
        {
            return uniqueToBase.TryGetValue(uniqueDef, out ThingDef baseDef) ? baseDef : null;
        }

        /// <summary>
        /// Whether the def is a unique weapon (has CompUniqueWeapon).
        /// </summary>
        public static bool IsUniqueWeapon(ThingDef def)
        {
            return def.HasComp(typeof(CompUniqueWeapon));
        }

        /// <summary>
        /// Resolves the base and unique ThingDefs for a weapon, regardless of
        /// whether the weapon is currently in its base or unique form.
        /// </summary>
        public static void ResolveWeaponDefs(Thing weapon, out ThingDef baseDef, out ThingDef uniqueDef)
        {
            if (IsUniqueWeapon(weapon.def))
            {
                uniqueDef = weapon.def;
                baseDef = GetBaseVariant(weapon.def);
            }
            else
            {
                baseDef = weapon.def;
                uniqueDef = GetUniqueVariant(weapon.def);
            }
        }
    }
}

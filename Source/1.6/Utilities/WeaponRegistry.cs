using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Caches base↔unique weapon pair mappings at startup and provides runtime
    // lookups for weapon pair resolution.
    public static class WeaponRegistry
    {
        private const string UniqueSuffix = "_Unique";

        private static Dictionary<ThingDef, ThingDef> baseToUnique;
        private static Dictionary<ThingDef, ThingDef> uniqueToBase;
        private static List<ThingDef> orphanUniqueDefs;

        // Builds the base↔unique weapon pair cache. Must be called during
        // StaticConstructorOnStartup (after all defs are loaded). A non-null
        // report absorbs any fatal exception so the rest of the mod can still
        // initialize; passing null preserves the throwing contract for direct
        // callers.
        public static void Initialize(InitDiagnostics report = null)
        {
            try
            {
                baseToUnique = new Dictionary<ThingDef, ThingDef>();
                uniqueToBase = new Dictionary<ThingDef, ThingDef>();
                orphanUniqueDefs = new List<ThingDef>();

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

                // One warning per orphan, after the scan completes, so the log
                // groups them together rather than interleaving with per-def
                // error spam from the catch above. Orphans stay customizable
                // via the IsUniqueWeapon HasComp check; what they lose is the
                // ability to revert to a base def when the trait list empties.
                foreach (ThingDef orphan in orphanUniqueDefs)
                {
                    Log.Warning("[Unique Weapons Unbound] No base weapon detected for "
                        + orphan.SourceForLog()
                        + "; customizable but cannot revert to base. "
                        + "Add a descriptionHyperlinks entry or use the '_Unique' suffix.");
                }

                WarnOnPairConflicts();
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
            if (baseDef == null)
            {
                orphanUniqueDefs.Add(def);
                return;
            }

            // Each unique def always knows its own base, so this side never collides.
            uniqueToBase[def] = baseDef;

            // The reverse mapping can collide: with mods installed, several unique
            // defs may resolve to the same base (e.g. multiple variants of a vanilla
            // weapon, or descriptionHyperlinks that all point back to it). Prefer the
            // one whose defName is exactly '{BaseDefName}_Unique'; failing an exact
            // match, keep the first registered and let load order decide the winner.
            if (!baseToUnique.ContainsKey(baseDef) || IsExactNamingMatch(def, baseDef))
                baseToUnique[baseDef] = def;
        }

        // Whether the unique def's name is exactly '{BaseDefName}_Unique' for
        // the given base — the strongest pairing signal, used to break ties
        // when several unique defs resolve to the same base weapon.
        private static bool IsExactNamingMatch(ThingDef uniqueDef, ThingDef baseDef)
        {
            return uniqueDef.defName == baseDef.defName + UniqueSuffix;
        }

        // Detects the base weapon for a unique weapon def. Primary:
        // descriptionHyperlinks. Fallback: naming convention.
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
            if (uniqueDef.defName.EndsWith(UniqueSuffix))
            {
                string baseName = uniqueDef.defName.Substring(0, uniqueDef.defName.Length - UniqueSuffix.Length);
                return DefDatabase<ThingDef>.GetNamedSilentFail(baseName);
            }

            return null;
        }

        // Logs one warning per base weapon that several unique defs resolved to
        // (a mod conflict the player owns). Only one variant wins the
        // base→unique mapping (see RegisterUniqueWeaponDef), so the rest can't
        // be produced by customizing the base weapon. Each variant is reported
        // with its source mod so players and modders know which defs to
        // reconcile. Grouped after the scan, like the orphan warnings.
        private static void WarnOnPairConflicts()
        {
            Dictionary<ThingDef, List<ThingDef>> variantsByBase = new Dictionary<ThingDef, List<ThingDef>>();
            foreach (KeyValuePair<ThingDef, ThingDef> pair in uniqueToBase)
            {
                if (!variantsByBase.TryGetValue(pair.Value, out List<ThingDef> variants))
                    variantsByBase[pair.Value] = variants = new List<ThingDef>();
                variants.Add(pair.Key);
            }

            foreach (KeyValuePair<ThingDef, List<ThingDef>> entry in variantsByBase)
            {
                if (entry.Value.Count < 2)
                    continue;

                ThingDef winner = baseToUnique[entry.Key];
                string variantList = string.Join(", ", entry.Value
                    .OrderBy(v => v.defName)
                    .Select(v => v.SourceForLog()));

                Log.Warning("[Unique Weapons Unbound] Base weapon " + entry.Key.SourceForLog()
                    + " maps to multiple unique variants (" + variantList + "); using "
                    + winner.defName + " as the base's unique form. This is a mod conflict; "
                    + "reconcile the mods or load order if that is not the intended variant.");
            }
        }

        // Returns the unique variant for a base weapon def, or null if none
        // exists.
        public static ThingDef GetUniqueVariant(ThingDef baseDef)
        {
            return baseToUnique.TryGetValue(baseDef, out ThingDef unique) ? unique : null;
        }

        // All registered unique-variant ThingDefs (one per base↔unique pair).
        // Used by the startup diagnostic to bucket pairs by source mod.
        public static IEnumerable<ThingDef> AllUniqueDefs => uniqueToBase.Keys;

        // Unique-comp ThingDefs that loaded with no detectable base weapon (no
        // descriptionHyperlinks pointing at a non-unique weapon and no matching
        // '{BaseDefName}_Unique' naming). Still customizable, but cannot revert
        // to base. Used by the startup diagnostic.
        public static IEnumerable<ThingDef> OrphanUniqueDefs =>
            orphanUniqueDefs ?? (IEnumerable<ThingDef>)System.Array.Empty<ThingDef>();

        // Returns the base weapon for a unique weapon def, or null if not
        // found.
        public static ThingDef GetBaseVariant(ThingDef uniqueDef)
        {
            return uniqueToBase.TryGetValue(uniqueDef, out ThingDef baseDef) ? baseDef : null;
        }

        // Whether the def is a unique weapon (has CompUniqueWeapon).
        public static bool IsUniqueWeapon(ThingDef def)
        {
            return def.HasComp(typeof(CompUniqueWeapon));
        }

        // Resolves the base and unique ThingDefs for a weapon, regardless of
        // whether the weapon is currently in its base or unique form.
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

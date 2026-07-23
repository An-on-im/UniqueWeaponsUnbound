using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Grammar;

namespace UniqueWeaponsUnbound
{
    public partial class Dialog_WeaponCustomization
    {
        // --- Weapon name regeneration ---

        private const int NameRegenMaxAttempts = 3;

        // Generates a random weapon name using vanilla's grammar system
        // (NameGenerator + RulePackDefOf.NamerUniqueWeapon), matching the same
        // code path used by CompUniqueWeapon.PostPostMake() for initial
        // generation. Returns null if generation fails after
        // NameRegenMaxAttempts attempts; callers should leave the name field
        // unchanged in that case.
        private string GenerateWeaponName()
        {
            Exception lastException = null;
            for (int attempt = 1; attempt <= NameRegenMaxAttempts; attempt++)
            {
                string name = null;
                try
                {
                    name = GenerateWeaponNameOnce();
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                if (!string.IsNullOrWhiteSpace(name) && name != "ErrorName")
                    return name;

                Log.Warning(BuildNameRegenFailureMessage(attempt, lastException));
            }

            Log.Warning("[Unique Weapons Unbound] Skipping weapon name auto-regeneration "
                + "after " + NameRegenMaxAttempts
                + " failed attempts; the existing name will be preserved.");
            return null;
        }

        private string GenerateWeaponNameOnce()
        {
            // Collect adjectives from all desired traits
            var adjectives = new List<string>();
            foreach (WeaponTraitDef trait in desiredTraits)
            {
                if (trait.traitAdjectives != null && trait.traitAdjectives.Count > 0)
                    adjectives.AddRange(trait.traitAdjectives);
            }

            // Get weapon type label from CompProperties_UniqueWeapon.namerLabels
            CompProperties_UniqueWeapon props = uniqueDef?.comps?
                .OfType<CompProperties_UniqueWeapon>()
                .FirstOrDefault();
            string weaponType = (props?.namerLabels != null && props.namerLabels.Count > 0)
                ? props.namerLabels.RandomElement()
                : "UWU_WeaponTypeFallback".Translate();

            string colorLabel = EffectiveColor?.label ?? "UWU_ColorFallback".Translate();

            GrammarRequest request = default;
            request.Includes.Add(RulePackDefOf.NamerUniqueWeapon);
            request.Rules.Add(new Rule_String("weapon_type", weaponType));
            request.Rules.Add(new Rule_String("color", colorLabel));
            if (adjectives.Count > 0)
                request.Rules.Add(new Rule_String("trait_adjective", adjectives.RandomElement()));

            // Publish the weapon's material as a "stuff_adjective" grammar symbol so a
            // companion material-namer (e.g. Unique Melee Weapons) can weave it into the
            // unique name — its own PostPostMake path can't reach us here, so it keys off
            // this rule instead. Inert on its own: nothing references [stuff_adjective]
            // unless such a mod's rulepack is present, so this is a harmless,
            // dependency-free data contract. Prefer stuffAdjective ("wooden", "golden"),
            // fall back to the label ("plasteel", "jade"). weapon.Stuff is null for
            // non-stuffable uniques (e.g. Odyssey's ranged ones) — nothing to publish.
            ThingDef stuff = weapon.Stuff;
            if (stuff != null)
            {
                string stuffAdjective = stuff.stuffProps?.stuffAdjective;
                if (stuffAdjective.NullOrEmpty())
                    stuffAdjective = stuff.label;
                request.Rules.Add(new Rule_String("stuff_adjective", stuffAdjective));
            }

            // Add the customizing pawn's name data for ANYPAWN_* grammar rules,
            // enabling possessive name patterns like "Kira's Gold Rifle"
            foreach (Rule rule in TaleData_Pawn.GenerateFrom(pawn).GetRules("ANYPAWN"))
                request.Rules.Add(rule);

            return NameGenerator.GenerateName(request, null, false, "r_weapon_name").StripTags();
        }

        // Builds a diagnostic message pointing the user toward the most likely
        // source of the failure: a malformed translation of the vanilla
        // NamerUniqueWeapon rule pack. The original raw rule string is
        // discarded by Rule_String when its regex parse fails, so we report the
        // count of rules whose keyword ended up null/empty alongside the active
        // language and the rule pack's owning mod.
        private static string BuildNameRegenFailureMessage(int attempt, Exception ex)
        {
            string langName = LanguageDatabase.activeLanguage?.FriendlyNameNative
                ?? LanguageDatabase.activeLanguage?.LegacyFolderName
                ?? "(unknown)";

            RulePackDef pack = RulePackDefOf.NamerUniqueWeapon;
            string packName = pack?.defName ?? "NamerUniqueWeapon";
            string ownerMod = pack?.modContentPack?.Name ?? "(unknown)";

            int badRuleCount = 0;
            if (pack != null)
            {
                try
                {
                    foreach (Rule rule in pack.RulesPlusIncludes)
                    {
                        if (rule == null || string.IsNullOrEmpty(rule.keyword))
                            badRuleCount++;
                    }
                }
                catch
                {
                    // Diagnostics are best-effort; never let them mask the original failure.
                }
            }

            string msg = "[Unique Weapons Unbound] Weapon name regeneration attempt "
                + attempt + "/" + NameRegenMaxAttempts + " failed."
                + " Active language: " + langName + "."
                + " RulePackDef '" + packName + "' (owned by '" + ownerMod + "').";

            if (badRuleCount > 0)
            {
                msg += " Detected " + badRuleCount + " malformed grammar rule(s) "
                    + "(parser left keyword null, original raw string is unrecoverable). "
                    + "Most likely cause: a translation/language mod is shipping malformed "
                    + "entries for " + packName + ".rulePack.rulesStrings — entries must use "
                    + "the form 'keyword(args)?->output' (e.g. 'weapon_noun(p=2)->[weapon_type]'), "
                    + "not a bare keyword like 'weapon_noun'. "
                    + "Check translation overrides for language '" + langName + "' "
                    + "in any installed language packs or translation mods.";
            }

            if (ex != null)
                msg += " Exception: " + ex;

            return msg;
        }
    }
}

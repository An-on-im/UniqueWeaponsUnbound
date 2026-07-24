---
name: translate
description: Generate, update, or audit mod localization (Keyed + DefInjected) for a target language, grounded in vanilla RimWorld terminology. Use when asked to add a language, update translations, or check translation freshness.
argument-hint: "[language, e.g. German | update | check]"
---

# Translate

Produce or refresh localization files for Unique Weapons Unbound. English is
the source of truth; every other language derives from it.

## Non-negotiables

- **Run the checker first and last.** `python3 Scripts/check-translations.py`
  validates key sets, placeholders, DefInjected paths, staleness, and file
  hygiene deterministically. Never hand-derive anything it reports; never
  finish with it failing.
- **Community translations are owned by their contributors.** Update
  stale/missing keys in an existing language when asked, but do not rewrite a
  contributor's phrasing wholesale without the user's explicit direction.
  (Russian is community-maintained — PR #6.)
- **Machine-assisted output is a first pass.** PRs and commits containing
  generated translations must say so and invite native-speaker review.

## File map and conventions

- English Keyed source: `1.6/Languages/English/Keyed/UWU_UI.xml`
- Target layout: `1.6/Languages/<Language>/Keyed/*.xml` and
  `1.6/Languages/<Language>/DefInjected/<DefTypeFolder>/*.xml`
- `<DefTypeFolder>` must be the def's resolvable type name: bare for vanilla
  types (`JobDef`, `ResearchProjectDef`), **namespace-qualified for this mod's
  own def classes** (`UniqueWeaponsUnbound.TraitCostRuleDef`) — a bare custom
  name silently drops every translation in the folder.
- DefInjected keys are `DefName.field` paths (`UniqueSmithing.description`,
  `UniqueSmithing.customUnlockTexts.0`). Translate `label`, `description`,
  `reportString`, `customUnlockTexts`, `generalRules.rulesStrings` — the
  checker warns on uncovered label/description.
- **EN comment convention (required):** every translated entry carries the
  current English source directly above it:
  `<!-- EN: Customize {0} -->` — this is how the checker detects staleness.
- Formatting: UTF-8 without BOM, LF endings, 2-space indent, final newline,
  root element `<LanguageData>`.
- Placeholders (`{0}`, `{1}`, named args) must match English exactly per key.
  Translator comments above placeholdered English keys explain what gets
  injected — injected values are lowercase def labels; phrase around them
  accordingly.

## Terminology grounding (do not skip)

Every game term must match the official localization, not a plausible
translation. Sources, in order:

1. Vanilla language data:
   `"$RIMWORLD_PATH"/Data/<Expansion>/Languages/<Language> (<Native>).tar`
   (read entries with `tar -xOf`). Check Core plus Odyssey (this mod's DLC),
   and Ideology for relic/ideoligion strings.
2. This file's glossary below (lessons already learned — apply them).
3. If a term appears nowhere official, flag it in the PR for native review
   rather than inventing silently.

Terms that MUST be grounded before use: weapon trait, unique weapon, relic,
ideoligion, charge weapons, quality tiers, workbench names (smithy, machining
table, fabrication bench), research project names, tech levels.

### Glossary — Russian (from PR #6 native review)

| English | Use | Never | Why |
|---|---|---|---|
| trait (weapon) | свойство | черта | vanilla `WeaponTraits`=Свойства; черта = pawn personality traits |
| ideoligion | идеолигия | идеология | vanilla's coined portmanteau (`ReformIdeoligion`) |
| charge (weapons) | энерг- root | заряд- | vanilla `Gun_ChargeRifle`=энерговинтовка; заряд reads as ammo |
| fueled/electric smithy | топливная кузня / электрокузня | кузница | vanilla building labels |
| machining table | верстак | обрабатывающий стол | vanilla building label |
| fabrication bench | высокоточный станок | сборочный стол | vanilla building label |
| Cancel (button) | Отменить | Отмена | vanilla `Cancel`; buttons use infinitive verbs |
| job report strings | noun phrases (Настройка {0}) | finite verbs | matches inspect-pane convention |

Add rows here whenever a native review lands corrections.

## Workflows

### Initial generation (`/translate <Language>`)

1. Run the checker; confirm English itself is clean.
2. Enumerate English Keyed keys and DefInjected-translatable def fields
   (mirror the English/Russian file structure).
3. Extract the vanilla tar for the target language into the scratchpad;
   build a term list for the grounded terms above.
4. Translate via subagent(s) carrying: the glossary, the vanilla term list,
   the EN-comment requirement, placeholder rules, and formatting rules.
   Chunk by file section if the key count is large.
5. Run the checker (`--strict` for new languages); fix everything.
6. Review the diff yourself before committing. Commit message and PR text
   must state machine-assisted origin and invite native review.

### Update pass (`/translate update`)

1. Run the checker; it lists missing keys and stale entries per language.
2. Translate only that delta, refreshing each entry's EN comment.
3. Leave correct existing entries untouched. Re-run the checker.

### Audit only (`/translate check`)

Run the checker and report; change nothing.

## Optional in-game verification

RimWorld Dev Mode offers "Save translation report" and "clean up translation
files" (Verse.LanguageReportGenerator / TranslationFilesCleaner). These need a
running game with the mod loaded — useful as a final QA pass, not a substitute
for the checker.

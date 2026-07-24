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

### Glossary — Japanese (machine-assisted generation, 2026-07; no native review yet)

Style rules discovered from the vanilla JP data (mandatory):

- Vanilla JP uses ASCII punctuation: `,` and `.` — never `、` or `。`.
- Descriptions/tooltips: polite です/ます form ending `.`; labels/buttons no period.
- Job report strings: continuous form 〜している / 〜中, no subject, no period.
- Quote injected def labels and cross-referenced UI labels with 「」.

| English | Use | Never | Why |
|---|---|---|---|
| trait (weapon) | 特性 | — | vanilla `WeaponTraits`=特性; unlike Russian, JP shares the pawn-trait word |
| unique weapon | ユニークな武器 | | vanilla `UniqueWeapon` |
| Pulse-charged munitions (ChargedShot research) | チャージライフル | パルス弾 | JP names the research after the rifle; disambiguate as 「チャージライフル」の研究 |
| fueled / electric smithy | 工作台 / 電動工作台 | 鍛冶場 | vanilla building labels |
| machining table | 精密工作機械 | | vanilla building label (also the Machining research name) |
| fabrication bench | コンポーネント工作台 | | vanilla building label |
| ultratech | 最先端の技術力 (noun) / 最先端技術級 (attributive) | ウルトラテック | vanilla `TechLevel_Ultra` |
| ideoligion | 思想 | イデオリギオン | JP does not coin a portmanteau; relic = レリック |
| Cancel / Confirm / Randomize / Reset | キャンセル / 了承 / ランダム / リセット | | vanilla Keyed buttons |

Mod-decided terms pending native review: research trio ユニーク武器の鍛冶 /
ユニーク武器の精密加工 / ユニーク武器の組立製造; haul planner modes 順次 / 巡回 /
徹底; net refund/cost 実質返却 / 実質コスト; haul plan 運搬計画.

### Cross-language lessons

- Wrap injected `{0}` def labels in the language's quote marks (JP 「{0}」,
  RU «{0}») — injected labels never inflect, and quoting sidesteps case and
  agreement problems.
- When an English string is reworded, refresh the EN comments in every
  language **in the same commit** — the checker reports the mismatch as STALE
  either way, but batching avoids churn.
- Coined vanilla terms (ideoligion) may be a portmanteau in one language
  (RU идеолигия) and a plain word in another (JP 思想) — always check, never
  extrapolate between languages.

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

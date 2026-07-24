# Contributing

Bug reports, fixes, and features are welcome — open an issue or pull request.
Build instructions are in [README.md](README.md); the mod builds with
`dotnet build UniqueWeaponsUnbound.sln -c Release`.

## Localization

The mod targets the languages below, chosen by RimWorld's per-language
audience size. Contributions for any other language RimWorld supports are
welcome too.

| Language | Status | Credit |
|---|---|---|
| English | Source | — |
| Simplified Chinese | Planned | |
| Russian | Native | [An-on-im](https://github.com/An-on-im) |
| Korean | Planned | |
| German | Planned | |
| Spanish | Planned | |
| French | Planned | |
| Brazilian Portuguese | Planned | |
| Japanese | Machine-assisted — native review welcome | |

Statuses: **Source** (the authoritative English strings), **Machine-assisted**
(generated with terminology grounded against the official RimWorld
localization; awaiting native review), **Native** (written or reviewed by a
native speaker), **Planned** (not started — contributions welcome).

### Contributing a translation

- Files live under `1.6/Languages/<Language>/` (`Keyed/` and `DefInjected/`),
  mirroring the structure of `1.6/Languages/English/`.
- Every translated entry carries the current English source in a comment
  directly above it, e.g. `<!-- EN: Customize {0} -->` — this is how stale
  translations are detected when the English changes.
- Placeholders (`{0}`, `{1}`, ...) must match the English exactly.
- This mod's custom def types use namespace-qualified DefInjected folder
  names (`UniqueWeaponsUnbound.TraitCostRuleDef`); vanilla types use bare
  names (`JobDef`, `ResearchProjectDef`).
- Formatting: UTF-8 without BOM, LF line endings, 2-space indent.
- Validate before opening a PR:

  ```bash
  python3 Scripts/check-translations.py --strict
  ```

  It checks key coverage, placeholders, DefInjected paths, staleness, and
  file hygiene.
- Improving a machine-assisted language? Corrections from native speakers
  are gladly merged, no matter how small.

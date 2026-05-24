# Changelog

All notable changes to Unique Weapons Unbound will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.2] - 2026-05-25

### Added

- Log a startup warning and surface an "Orphan Unique Weapons" row in startup diagnostics for unique weapons with no detectable base (mod-author visibility)
- Dev diagnostics for tech-level gating, with hardened tech-level enforcement

### Fixed

- Invalidate Verb burst caches when traits change so cached projectile/burst behavior refreshes
- Close double-recovery window in the weapon return toil
- Prevent phantom abilities on customized weapons
- Surface no-compatible-traits state in the customization dialog
- Surface ingredient reservation failures at confirm time
- Surface bail messages on silent customization paths
- Detect Alpha Armoury API drift without nagging users who don't have it
- Surface CompUniqueWeapon reflection failures at startup

### Changed

- Isolate dialog open and per-frame render failures
- Harden research-tab filter against throws and reduce per-frame work
- Verify and harden remaining reflection sites
- Isolate finalize-toil failures from the job error path
- Isolate ApplyOperation failures per customization op
- Isolate ability heal failures at job start
- Isolate gizmo build and click failures
- Isolate float menu failures per weapon entry
- Isolate Initialize failures across subsystems
- Narrow weapon gizmo patch to CompForbiddable
- Consolidate tech-level gate around configured ceiling
- Align UWU_Settings field lists to UI section order
- Split JobDriver_CustomizeWeapon into phase partials

## [1.2.1] - 2026-05-12

### Added

- Alpha Armoury weapon tweaking kits now contribute their stored trait to the discovered-trait pool

## [1.2.0] - 2026-05-07

### Added

- New setting to restrict trait additions to discovered weapons' traits only
- Trait cost multiplier slider now goes up to 500% (previously 300%)
- Customization dialog now caches available ingredient counts for its lifetime, reducing per-frame work

## [1.1.0] - 2026-05-06

### Added

- Sweep + Held-Karp haul planner is now the default. Pawns batch ingredient pickups using both the carry tracker and inventory, dramatically cutting trips for resource-heavy customizations
- Customize-weapon jobs now save and resume correctly mid-round (job state and spec are scribed)
- New "Ingredient hauling" settings section to choose the planner. Sequential remains as a safe fallback
- xUnit test project with Sequential and Sweep coverage

### Fixed

- Static-init scans and the runtime cost pipeline now isolate per-def errors, so a single malformed third-party def can no longer silently break the mod (float menu options going missing). Errors are logged with the originating mod for bug-report routing

### Changed

- Internal refactor: introduced a haul planner abstraction (`IHaulPlanner` plus DTOs), and split `WeaponModificationUtility` into focused helpers

## [1.0.4] - 2026-05-01

### Fixed

- Drafted pawns can no longer use forbidden ingredients to pay customization costs
- Preserve ability charges across customization operations
- Clear stale ability and accuracy caches when traits change
- Survive grammar resolver failures during name regeneration

### Changed

- Repositioned Unique Fabrication on the research tree

## [1.0.3] - 2026-04-29

### Fixed

- Surface accurate bails for haul-phase placement edge cases
- Track placed ingredients via `placedAction` callback

## [1.0.2] - 2026-04-28

### Added

- Minimum weapon quality threshold gate for customization

### Fixed

- Customization now aborts cleanly when placed ingredients are lost mid-job
- Ingredient stacks that fail to reserve mid-job are now skipped instead of stalling
- Unreachable materials are now reported as availability failures
- Customization job failures are now surfaced to the player
- When the weapon is lost simultaneously with another failure, the weapon-loss message takes priority

## [1.0.1] - 2026-04-21

### Added

- Setting to disable trait count limit
- Search bar in customize dialog traits tab
- Settings for recipe base cost and ground menu toggles

### Fixed

- Texture variant grid now scrolls when overflowing the tab area

## [1.0.0] - 2026-04-15

### Added

- Customize unique weapons at workbenches — add, remove, and swap traits
- Three customization entry points: equipped, inventory, and ground items
- Weapon gizmo for ground-item customization
- Asymmetric cost pipeline with tech-level fallback and data-driven trait cost rules
- Negative trait economics and thematic cost rules
- Global cost multiplier setting and configurable refund rate
- Research toggle and settings to bypass workbench tech-level and crafting research requirements
- Mod settings panel with sections and smart controls
- Multi-palette color picker with Ideology DLC support
- Relic Ideology color overlay and stacking tooltips
- Relic weapon name locking in customization dialog
- Support for unique weapons without a base weapon variant
- Full localization support — all UI strings extracted into keyed files

[1.2.2]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.2.2
[1.2.1]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.2.1
[1.2.0]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.2.0
[1.1.0]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.1.0
[1.0.4]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.0.4
[1.0.3]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.0.3
[1.0.2]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.0.2
[1.0.1]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.0.1
[1.0.0]: https://github.com/sam-hunt/UniqueWeaponsUnbound/releases/tag/v1.0.0

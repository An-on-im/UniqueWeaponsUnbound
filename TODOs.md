# TODOs

## Features

- Replace xml doc comments with regular strings
- git blame ignore for the xml doc comment replacement commit

- Blood-soaked trait rule requiring hemogen if biotech and UMW are installed?
- Optional reinforced barrel replacement rule for underbarrel traits?
- Random button?
- Mod setting to disable trait customization?
- Dev mode mod settings page button to copy settings to clipboard?
- Extend XML WeaponTraitCostDef schema/workers
- Add Alpha Armory rules support
- Mod setting to scale trait limit with quality.
- Mod setting for chance of upgrading enemy spawn weapons to unique
  - increasing chance of biocoding at higher tech/quality
- Free Customize unique weapon relics on form/reform ideology
- Free Customize unique weapon dev mode gizmo
- Multiplayer support
- Explore dynamically preserving arbitrary mod-added weapon properties across a
  base<->unique def conversion (today WeaponDefConversion hand-copies a fixed
  set: stuff, quality, hp%, texture, biocoding, art, relic status — anything
  else a mod attaches is dropped)
- Explore bladelink/persona weapons more thoroughly (currently explicitly
  excluded/skipped from customization)

## Cleanup

- split out any oversize files?
- small optimization: MayRequire VE Weapons attribute on some trait rules?

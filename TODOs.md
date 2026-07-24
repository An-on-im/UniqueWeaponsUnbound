# TODOs

## Features

- Mod option for gating customization on colony crafting skill recipe +2 or 12
- Blood-soaked trait rule requiring hemogen if biotech and UMW are installed?
- Monomolecular, plasma, zeus rules for UMW
- Extend XML WeaponTraitCostDef schema/workers
- Add Alpha Armory rules support
- Mod setting for chance of upgrading enemy spawn weapons to unique
  - increasing chance of biocoding at higher tech/quality?
- Free Customize unique weapon relics on form/reform ideology
- Free Customize unique weapon dev mode gizmo
- Multiplayer support?
- Explore dynamically preserving arbitrary mod-added weapon properties across a
  base<->unique def conversion (today WeaponDefConversion hand-copies a fixed
  set: stuff, quality, hp%, texture, biocoding, art, relic status — anything
  else a mod attaches is dropped)

## Cleanup

- split out any oversize files?
- small optimization: MayRequire VE Weapons attribute on some trait rules?

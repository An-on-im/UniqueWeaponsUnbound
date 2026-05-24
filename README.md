# Unique Weapons Unbound

> A RimWorld mod for customizing unique weapons

[![RimWorld](https://img.shields.io/badge/RimWorld-1.6-blue.svg)](https://rimworldgame.com/)
[![Odyssey DLC](https://img.shields.io/badge/DLC-Odyssey-blue.svg)](https://store.steampowered.com/app/2380740/RimWorld__Odyssey/)
[![Version](https://img.shields.io/badge/Version-1.2.2-brightgreen.svg)](https://github.com/sam-hunt/UniqueWeaponsUnbound/releases)
[![Development Status](https://img.shields.io/badge/Status-Released-brightgreen.svg)](https://github.com/sam-hunt/UniqueWeaponsUnbound/releases)

![Preview](About/Preview.png)

## About

RimWorld's Odyssey DLC introduced unique weapons — special variants of existing weapons with 1-3 weapon traits that alter properties like accuracy, weight, name, color, or grant abilities. But once you find one, you're stuck with whatever traits it rolled.

This mod lets you take control. Customize traits, colors, textures, and names at crafting workbenches — with full vanilla validation, research-gated progression, and configurable balance settings.

## Features

### Trait Customization

- **Add traits** to weapons at crafting workbenches, converting normal weapons into unique variants
- **Remove traits** from existing unique weapons, reverting them if all traits are removed
- **Full trait validation** respecting vanilla rules — max count, category exclusions, sole-trait restrictions
- **Dynamic resource costs** based on recipes, quality, thematic keywords, and more

### Color, Texture & Name

- **Color selection** across three palettes: weapon colors, structure colors, and Ideology colors (when DLC is active, including favorite and ideo color indicators)
- **Texture variants** for weapons with multiple visual styles
- **Name customization** with auto-generated names using vanilla grammar, or type your own
- **Trait-forced colors** are respected — when a trait dictates a color, the picker shows which trait controls it

### Ideology DLC Support

- **Relic handling**: Ideology relics can be customized while preserving relic status; the relic name is locked and managed through ideology reform
- **Ideo & favorite color overlays**: The color picker highlights your pawn's ideo color and favorite color, matching the vanilla styling station

### Multiple Entry Points

- **Weapon gizmo**: Select any weapon on the ground and click the customize button, then choose a colonist
- **Workbench right-click**: Right-click a workbench with a colonist selected to customize their equipped or carried weapons
- **Ground weapon right-click**: Right-click a weapon on the ground to send a colonist to customize it at the nearest suitable workbench

### Workbench & Research

- **Tiered workbenches**: Smithy (Neolithic/Medieval), Machining Table (up to Industrial), Fabrication Bench (all tiers)
- **Research progression**: Basic (Smithing), Standard (Machining), and Advanced (Advanced Fabrication) weapon customization
- **Job-based system**: Each change is a separate crafting job — interrupt safely without losing resources

### Mod Settings

All balance levers are configurable from the in-game mod settings:

- **Trait cost multiplier** (0–300%) — scale all trait costs up or down, or disable them entirely
- **Trait refund rate** (0–100%) — control how much material is returned when removing traits
- **Research toggles** — disable customization research requirements or weapon crafting research prerequisites
- **Workbench restrictions** — allow any weapon-crafting workbench regardless of tech level
- **Tech-level gates** — independently allow or block ultratech and archotech weapon customization
- **Uncraftable weapons** — allow customization of weapons with no crafting recipe
- **Sole-trait enforcement** — optionally enforce vanilla's sole-trait generation restrictions during customization

### Mod Compatibility

Designed for automatic compatibility with mods that add new unique weapons, weapon traits, or crafting recipes. No hard-coded def references — all detection is dynamic.

## Requirements

- **RimWorld 1.6** or later
- **Odyssey DLC** (required - depends on Odyssey's unique weapon system)
- **Harmony** (auto-download from Steam Workshop if you don't have it)

## Installation

### Steam Workshop (Recommended)

Subscribe on the Steam Workshop and it will auto-download.

### Manual Installation

1. Download the latest release from the [Releases](https://github.com/sam-hunt/UniqueWeaponsUnbound/releases) page
2. Extract the `UniqueWeaponsUnbound` folder to your RimWorld `Mods` directory:
   - **Windows**: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\`
   - **Mac**: `~/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/`
   - **Linux**: `~/.steam/steam/steamapps/common/RimWorld/Mods/`
3. Enable the mod in RimWorld's mod menu
4. Restart RimWorld

## Compatibility

- **Safe to add** to existing saves.
- **Safe to remove** from saves (no persistent game state modifications).

## Contributing

Bug reports and feature requests welcome on [GitHub Issues](https://github.com/sam-hunt/UniqueWeaponsUnbound/issues).
Please attach any relevant logs/stack traces/mod lists etc.

For development setup, see [CLAUDE.md](CLAUDE.md).

## Credits

**Author**: Sam Hunt ([@sam-hunt](https://github.com/sam-hunt))

**Built With**:

- [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike - Runtime patching library
- RimWorld modding API, community examples

**Special Thanks**:

- [Ludeon Studios](https://ludeon.com) for RimWorld and modding API
- [The RimWorld modding community](https://steamcommunity.com/app/294100/workshop/) for inspiration and working examples
- [Claude Code](https://claude.com/claude-code) for `monodis`, `ilspycmd` and C#

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Unique Weapons Unbound** is a RimWorld 1.6 mod that allows players to customize unique weapons. Requires the Harmony mod and the Odyssey DLC.

**Key Technologies:** C# (.NET Framework 4.7.2), Harmony library, RimWorld modding API, XML definitions

## Build Commands

```bash
# Build the mod (outputs to 1.6/Assemblies/ AND atomically redeploys to the RimWorld Mods folder)
dotnet build UniqueWeaponsUnbound.sln -c Release

# Build only the main project (also triggers the deploy)
dotnet build Source/1.6/UniqueWeaponsUnbound.csproj

# Clean build artifacts
dotnet clean UniqueWeaponsUnbound.sln

# Override RimWorld install path
RIMWORLD_PATH="/path/to/RimWorld" dotnet build UniqueWeaponsUnbound.sln -c Release
# Or: dotnet build -p:RimWorldPath="/path/to/RimWorld"
```

The build system auto-detects the RimWorld installation path on Windows/Linux/Mac (including WSL targeting a Windows install). For CI builds without RimWorld installed, it falls back to the `Krafs.Rimworld.Ref` NuGet package. For local development and api inspection (monodis, ilspycmd etc), the local installation should be preferred as the source of truth.

### Deployment

Every local build auto-deploys into the RimWorld `Mods/` folder (when a local install is detected) — no separate clean/copy step. The `StageMod` target in `Source/1.6/UniqueWeaponsUnbound.csproj` is the **single source of truth** for what ships: it wipes the target dir and recopies a whitelist of runtime file types, so deleted/renamed files never linger. To change what ships, edit its `_ModFiles` ItemGroup. CI (`.github/workflows/release.yml`) and the local Stop hook both reuse this target, so the release zip can't drift from the local deploy.

A gitignored Stop hook (`.claude/hooks/sync-mod.sh`) rebuilds + redeploys after any turn that touched mod source/content.

**WSL Setup:** Requires `RIMWORLD_PATH` env var in `~/.bashrc` pointing to the Windows RimWorld install (e.g., `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld`).

**Releases:** Push a tag matching `v*.*.*` to trigger the release workflow (`.github/workflows/release.yml`).

### Tests

xUnit suite under `Tests/1.6/` (a separate project, never shipped). Run with `./Scripts/test-windows.sh` — WSL can't host the net472 runner, so it shells out to the Windows `dotnet` CLI. CI builds but doesn't run it.

## Architecture

### Entry Point

`Source/1.6/Core/ModInitializer.cs` - Static constructor with `[StaticConstructorOnStartup]` auto-patches via Harmony attribute discovery. Harmony ID: `shunter.uniqueweaponsunbound`.

### Mod Structure

```
About/About.xml     # Mod metadata, dependencies, load order
LoadFolders.xml     # Tells RimWorld to load root (/) and 1.6/

Source/1.6/
├── Core/           # ModInitializer (Harmony bootstrap)
├── Properties/     # AssemblyInfo

1.6/
├── Assemblies/     # Build output (DLL) — gitignored
├── Defs/           # XML definitions (ThingDefs, etc.)
├── Patches/        # XML patches (XPath-based)
```

### Key Patterns

**Harmony Patching:** All patches use `[HarmonyPatch]` attributes for automatic discovery. Patches are organized by target class in subdirectories under `Source/1.6/`.

**Namespace Convention:** Use `*Patches` suffix for patch namespaces to avoid RimWorld type conflicts.

**Comments:** Plain `//` comments only — never XML doc comments (`///` with `<summary>` etc.). No tooling consumes the doc XML in this project, so the tag scaffolding is noise.

**Serialized Fields:** Use camelCase for fields serialized via `Scribe_Values.Look` to match save file XML element names (per .editorconfig). PascalCase for all other public members.

**Settings Triple Invariant (`UWU_Settings.cs`):** Every settings field must appear in three places with matching defaults: (1) field declaration, (2) `ResetToDefaults()`, (3) `ExposeData()`'s `Scribe_Values.Look` default. Missing a spot fails silently — drops from save, skips reset, or drifts from declared default. All three lists are kept in the UI's display order (the section ordering from `UWU_Mod.DoSettingsWindowContents`) with section comments, so a diff across the three blocks lines up row-for-row. When adding/removing/renaming a setting, update all three and slot it into its UI section.

## Debugging

1. **Enable RimWorld Dev Mode:** Settings → Dev Mode → Logging
2. **Log locations:**
   - **Windows:** `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`
   - **WSL:** `/mnt/c/Users/*/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`
   - **Linux (Steam):** `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`
3. **Logging:** Use `Log.Message("[Unique Weapons Unbound] ...")` for mod-specific logs
4. **Inspect RimWorld API:** `monodis "/mnt/c/.../RimWorldWin64_Data/Managed/Assembly-CSharp.dll"`

## Harmony Patch Examples

**Postfix Pattern:**

```csharp
[HarmonyPatch(typeof(TargetClass), nameof(TargetClass.MethodName))]
public static class TargetClass_MethodName_Postfix
{
    [HarmonyPostfix]
    public static void Postfix(TargetClass __instance, ref ReturnType __result)
    {
        // __instance: object method was called on
        // __result: return value (modifiable with ref)
    }
}
```

**Prefix Pattern (for skipping original):**

```csharp
[HarmonyPrefix]
public static bool Prefix(ref ReturnType __result)
{
    __result = newValue;
    return false; // Skip original method
}
```

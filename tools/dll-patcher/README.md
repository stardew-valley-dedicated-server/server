# DLL Patcher

> **Note**: This is an early version created to verify feasibility. While functional, it needs refactoring, cleanup and testing.

This tool patches the Stardew Valley game DLL during container startup to remove the `InitializeSounds` call from the `Game1.Initialize()` method.

## What it does

The patcher uses Mono.Cecil to modify the IL bytecode of `Stardew Valley.dll` and removes this call:

```csharp
DoThreadedInitTask(InitializeSounds);
```

Located at approximately line 2714 in `Game1.cs:Initialize()`.

## How it works

1. **Image Build**: The patcher is compiled into `/opt/dll-patcher/`
2. **Container startup**: `startapp.sh` runs the patcher on `Stardew Valley.dll`
   - Backs up original DLL on first run
   - Tracks patcher version via hash
   - Re-patches if patcher changed
3. **Game runtime**: SMAPI and mods load, game runs with patched DLL

## Why this is needed

Dedicated servers run headless and don't need sound. However, `Game1.Initialize()` calls `InitializeSounds` before SMAPI loads, making it impossible to patch via Harmony. Sound initialization in headless environments can cause crashes or dependency issues. This patcher modifies the DLL before the game starts, removing the problematic call entirely.

## Manual usage

```bash
dotnet run --project SDVPatcher.csproj -- "$GAME_PATH/Stardew Valley.dll"
```

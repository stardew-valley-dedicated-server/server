# Directory Reorganization

## Planned Change

Consolidate local development artifacts into a single `.junimoserver/` directory for better organization.

## Current Structure

```
/.output/          # Build output (compiled mod DLLs)
/.game-files/      # Downloaded Stardew Valley game files
/decompiled/       # Decompiled SDV source code
```

## Proposed Structure

```
/.junimoserver/
  ├── build/          # replaces .output (mod build artifacts)
  ├── game-files/     # replaces .game-files (downloaded game)
  └── decompiled/     # replaces /decompiled (decompiled source)
```

## Files to Update

- `.gitignore` - Replace individual entries with `/.junimoserver/`
- `tools/decompile-sdv.sh:50` - Update decompiled output path
- `mod/JunimoServer/JunimoServer.csproj:59` - Update commented .output path (if uncommented)
- Any developer documentation referencing these paths

## Benefits

- Better organization - all dev artifacts in one place
- Simpler .gitignore maintenance
- Clearer project structure
- Easier cleanup (single directory to remove)

## Status

Not yet implemented - marked for future organizational improvement.

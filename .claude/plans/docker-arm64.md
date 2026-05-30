# Native ARM64 Stardew Valley - Implementation Guide

> **GitHub Issue:** [#63 - linux/arm64/v8 support](https://github.com/stardew-valley-dedicated-server/server/issues/63)

## Core Concept
- ✅ Game DLLs are platform-independent C# IL bytecode
- ❌ Need ARM64 native libraries (SDL2, OpenAL, etc.)
- ❌ Need ARM64 .NET/Mono runtime

## Required ARM64 Native Dependencies

### FNA/MonoGame Runtime Libraries
- `libSDL2-2.0` (graphics/input)
- `libSDL2_image` (image loading)
- `libopenal1` (audio)
- `libtheorafile` (video codec)
- `libfna3d` (3D graphics layer)

### System Libraries
- Standard glibc ARM64 (Debian already has this)
- X11 libraries (ARM64 versions)

## .NET Runtime
- Install ARM64 Mono OR .NET Runtime
- Verify version compatibility with SMAPI (4.1.10 currently)
- Test: `dotnet --info` should show ARM64

## SMAPI Considerations
- SMAPI is also C# (IL bytecode)
- Should work with ARM64 runtime
- Verify SMAPI installer detects ARM64 runtime correctly
- May need to manually place SMAPI files if installer fails

## Docker Implementation Steps

### 1. Modify Dockerfile
- Add architecture detection: `ARG TARGETARCH`
- Conditional package installation based on `$TARGETARCH`

### 2. For ARM64 builds
```dockerfile
RUN if [ "$TARGETARCH" = "arm64" ]; then \
    apt-get install -y \
      libsdl2-2.0-0 \
      libsdl2-image-2.0-0 \
      libopenal1 \
      mono-runtime; \
fi
```

### 3. Download game via SteamCMD (still x86)
- Keep existing game download (app 413150)
- Extract DLLs and content (platform-independent)

### 4. Runtime wrapper
- Detect if running on ARM64
- Use ARM64 runtime to execute game DLLs
- Ensure native libs are in `LD_LIBRARY_PATH`

## Testing & Validation

- [ ] Verify all native libs load: `ldd StardewValley.dll`
- [ ] Test headless server startup
- [ ] Verify SMAPI mod loading works
- [ ] Performance benchmark vs x86 QEMU
- [ ] Test on actual ARM hardware (RPi 4/5, Mac M-series)

## Potential Blockers

- FNA native libs might not be in Debian ARM64 repos (may need manual build)
- SMAPI installer might be x86-only (need manual SMAPI installation)
- Some native interop in mods might break (rare)

## Quick Win: Test First

Before Docker changes, test locally on ARM hardware:

```bash
# On ARM64 Linux
apt install mono-runtime libsdl2-2.0-0 libopenal1
mono StardewValley.dll
```

---

**Start with local testing before Docker implementation.**

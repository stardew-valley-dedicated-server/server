# Decompiling Stardew Valley

Advanced topic covering game decompilation for mod development.

## Why Decompile?

In most cases, decompiling Stardew Valley isn't necessary for server operation. Visual Studio provides on-the-fly access to decompiled game code through its **Peek Definition** feature, which is sufficient for most development tasks.

However, decompiling can be useful if you need to:

- Explore game logic in detail for mod development
- Work with game code in a separate project
- Debug complex server-side interactions
- Develop advanced server modifications

## Long-term Goals

Recompiling the game is currently exploratory work rather than a fully supported workflow. However, having a fully recompilable version of Stardew Valley could enable:

- **Custom debugging symbols** (*.pdb files) for step-through debugging with breakpoints inside Stardew Valley code
- **Internal .NET upgrades** potentially improving performance and enabling access to newer C# language features for server-side mods
- **Advanced customizations** beyond what SMAPI allows

::: warning Experimental
This should be viewed as exploratory documentation of what might be achievable, rather than a supported or fully functional workflow.
:::

## Decompiling the Game

To decompile Stardew Valley, run the decompilation script:

```sh
bash ./tools/decompile-sdv.sh
```

This script will use decompilation tools to extract the C# source code from the Stardew Valley assemblies.

## Recompiling (Work in Progress)

::: warning Work in Progress
The recompilation process is still experimental and may require significant troubleshooting.
:::

If you want to attempt recompiling the decompiled code:

### 1. Decompile the Game

Follow the decompilation steps above.

### 2. Fix Compiler Errors

The decompiled code will likely have immediate compiler errors. For version 1.6.15, this typically involves:
- Commenting out problematic code sections
- Fixing 2-3 references to non-existent dependencies
- Addressing type conversion issues

### 3. Create global.json

In the decompilation root directory, create a `global.json` file:

```json
{
  "sdk": {
    "version": "6.0.403"
  }
}
```

### 4. Adjust Project Settings

Modify the `.csproj` file to use preview language features:

```xml
<LangVersion>preview</LangVersion>
```

### 5. Copy Game Files

Copy remaining files from your Steam installation into `<decompRoot>/bin`.

## Common Fixes

Here are some common fixes needed when recompiling:

### HashUtility Fix

The `HashUtility` class needs to be rewritten to use `System.IO.Hashing.XxHash32` instead of the deprecated `System.Data.HashFunction`:

```csharp
using System;
using System.IO.Hashing;
using System.Text;

namespace StardewValley.Hashing;

public class HashUtility : IHashUtility
{
    public int GetDeterministicHashCode(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        byte[] data = Encoding.UTF8.GetBytes(value);
        return GetDeterministicHashCode(data);
    }

    public int GetDeterministicHashCode(params int[] values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        byte[] array = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, array, 0, array.Length);
        return GetDeterministicHashCode(array);
    }

    public int GetDeterministicHashCode(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        byte[] hash = XxHash32.Hash(data);
        return BitConverter.ToInt32(hash, 0);
    }
}
```

### PropertyType Fixes

If you encounter type conversion issues with `PropertyType`, try adding `.ToString()` to the affected conversions.

## Development Best Practices

When working on advanced JunimoServer customizations:

- **Use version control** - Always commit your changes to git
- **Test thoroughly** - Test changes in a development environment first
- **Document your work** - Keep notes on modifications and fixes
- **Share findings** - Contribute your discoveries back to the community
- **Stay updated** - Game updates may break your customizations

## Contributing

If you've successfully implemented advanced features or found solutions to recompilation issues, please consider [contributing](/developers/contributing/) your findings to help other developers.

# EastwardLib

English | [한국어](README.ko.md)

Use `EastwardLib.dll` to work with Eastward assets and GArchives.

For CLI tools, see [EastwardPacker](https://github.com/VriskaSerket51/EastwardPacker).

## How to build

Requires .NET 10.

```sh
dotnet build -c Release
```

## How to use

### GArchive

Add a file to an archive:

```cs
using EastwardLib.Assets;

var g = GArchive.Read("locale.g");
g["locale/foo"] = new TextAsset("bar").Encode();
g.Write("locale_new.g");
```

Extract files:

```cs
var g = GArchive.Read("locale.g");
g.ExtractTo("./locale_extracted");
```

### AssetManager

Use the index to extract assets with their original names:

```cs
using EastwardLib.Assets;
using EastwardLib.Utils;

var assetManager = AssetManager.Create("./content/game/config.g");
assetManager.RootDirectory = "./content/game";
foreach (var file in Directory.GetFiles(assetManager.RootDirectory, "*.g"))
{
    assetManager.RegisterArchive(file);
}
assetManager.LoadAssets();
assetManager.ExtractTo("./exported");
```

Register DLC archives after the base archives. Use `LoadAsset(path)` to load an individual asset.

### Metadata

Use `EditMetadata()` to edit asset, script and texture indexes, then save them with `SaveMetadata()`.

```cs
var config = GArchive.Read("config.g");
assetManager.EditMetadata((assets, scripts, textures) =>
{
    scripts.SetModule("example", "scripts/example.lua", "script/example");
});
assetManager.SaveMetadata(config);
config.Write("config_new.g");
```

Renaming an index entry does not change references inside resource files.

### Source restoration and compilation

Restore sources:

```cs
new ProjectReconstructor(assetManager).SaveTo("./restored");
```

Sources are saved in `game/asset`.

Compile edited sources into archives:

```cs
using EastwardLib.Compilation;

var compiler = new AssetCompiler(assetManager);
var resources = compiler.Compile("script/example.sq", "./edited/example.sq");
compiler.SaveTo("./compiled", resources);
```

Use `CompileProject()` for an entire restored tree. Keep `.eastward-project.json` in the project root; it contains the path mappings needed for recompilation. Write the result to a new directory.

### ModPackage

Package compiled changes as an `.ewmod`:

```cs
using EastwardLib.Modding;

var mod = ModPackage.Create("Example", assetManager, resources);
mod.SaveTo("example.ewmod");
```

Use `Read()` and `Validate()` to check the package and original resources. `ApplyTo()` writes new `.g` archives. Use EastwardModLoader to install `.ewmod` packages directly.

## LuaJIT decompiler

Connect a decompiler when extracting Lua source:

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/native-library");
assetManager.LuaDecompiler = decompiler.Decompile;
```

Requires CMake 3.20 or newer, a C++20 compiler and the [decompiler source](https://github.com/VriskaSerket51/node-luajit-decompiler).

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

Pass the built library path to `NativeLuaDecompiler`.

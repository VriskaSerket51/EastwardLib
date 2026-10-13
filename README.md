# EastwardLib

By importing "EastwardLib.dll", you can handle Assets and GArchives.
<br/>
For more description, please check [here](https://github.com/VriskaSerket51/EastwardPacker).

## How to build

You need .NET 10 to build this library.

```sh
dotnet build -c Release
```

## How to use

### GArchive

You can extract files, insert new assets, and save ".g" archives.

```cs
using EastwardLib.Assets;

var g = GArchive.Read("locale.g");
g["locale/foo"] = new TextAsset("bar").Encode();
g.Write("locale_new.g");
g.ExtractTo("./locale_extracted");
```

You can create an empty archive with `GArchive.Create(name)`, and merge archives with `AddOverlay()`. Later entries take priority. Use `leaveOpen` when you want to keep an input stream open.

### AssetManager

You can load indexed assets and extract them with their original filenames.

```cs
using EastwardLib.Assets;
using EastwardLib.Utils;

var assetManager = AssetManager.Create("./content/game/config.g");
assetManager.RootDirectory = "./content/game";
foreach (var file in Directory.GetFiles(assetManager.RootDirectory, "*.g"))
{
    assetManager.LoadArchive(GArchive.Read(file));
}
assetManager.LoadAssets();
assetManager.ExtractTo("./exported");
```

> **Warning**
> `LoadAssets()` loads all indexed assets into memory and stops if an asset fails to load. You can use `LoadAsset(path)` to load assets separately.

You can load DLC archives with `LoadArchive()` after loading the base archives. Use `RegisterLoader()` to add or replace an asset loader.

Use `RegisterArchive(path)` to load an archive on its first resource request. Register base files before overlays. Failed pending overlays leave loaded archives unchanged and can be retried.

### Metadata

You can edit asset, script and texture indices, and save them back into "config.g".

```cs
using System.Text.Json.Nodes;
using EastwardLib.Assets;
using EastwardLib.Utils;

var config = GArchive.Read("config.g");
var assetManager = AssetManager.Create("config.g");
assetManager.EditMetadata((assets, scripts, textures) =>
{
    var info = assets["texture/example.png"];
    info.Tags ??= new JsonArray();
    info.Tags.Add("modded");
    scripts.SetModule("example", "scripts/example.lua", "script/example");
});
assetManager.SaveMetadata(config);
config.Write("config_new.g");
```

`EditMetadata()` validates all changes before applying them. Failed edits leave the original metadata unchanged. Successful edits refresh lookups and clear loaded assets.

- `AssetIndex`: `Add()`, `Remove()`, `Rename()`.
- `ScriptLibrary`: `SetModule()`, `RemoveModule()`.
- `TextureIndex`: `Update()`, `AddGroup()`, `AddTexture()`, `MoveTexture()`, `RemoveGroup()`, `RemoveTexture()`.

You can access the original JSON through `Data`. Unknown fields, field types, empty tables and texture map entries are retained. JSON formatting may change, and packed numbers use MessagePack precision. When editing nested JSON directly, use `EditMetadata()` or call `RefreshMetadata()` afterwards, then retrieve entries again.

Each index has `ToJson()` and `SaveTo(path)`. `AssetIndex.FromPacked()` and `Encode(packed: true)` support packed indices. `SaveMetadata(assetPath, scriptPath, texturePath)` saves separate files. `SaveMetadata(archive)` also updates an existing packed index and keeps other archive entries. Conflicting JSON and packed indices are rejected.

New texture entries need complete serialized objects. You can clone an existing entry to keep its schema. Removing a texture detaches its references but keeps map entries. Renaming an asset subtree updates index paths and parents; resource files and references inside them are not renamed.

### ProjectReconstructor

You can restore source assets for a matching Gii project.

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/eastward-luajit.dll");
assetManager.LuaDecompiler = decompiler.Decompile;
var report = new ProjectReconstructor(assetManager).SaveTo("./restored");
```

The output contains `game/asset` and a separate `restored.recovery.json` report. Use a new output directory. Missing resources and changed paths are recorded in the report; unexpected conversion errors throw.

- Scenes, prototypes, SQ scripts, PO localization, banks and meshes.
- Separate ASE animations with tags, layers, offsets and anchors.
- PSD textures and tilesets with available layers.
- Raw `.deckpack` folders with geometry, color textures and normal maps.
- XLS workbooks and GraphML for FSM, quest, portal and story graphs.

Source references are updated for restored package paths. XLS files retain available values using BIFF8; formulas and formatting removed from the game data cannot be restored. The writer supports 65,536 rows, 256 columns, 32,767 UTF-16 code units per cell and a CFB stream of about 7 MiB.

Original project settings, Gii/AKU binaries and discarded editing history are not included. You need the matching editor environment to open and build the full project. Quest transition fields need `tests/import_quest_graph.py` with the public Gii importer.

### AssetCompiler

You can compile edited source assets and save them into copies of the original archives.

```cs
using EastwardLib.Compilation;

var compiler = new AssetCompiler(assetManager);
var resources = compiler.Compile("script/example.sq", "./edited/example.sq");
compiler.SaveTo("./compiled", resources);
```

Use `CompileProject("./restored")` for a restored source tree with its recovery report. Load the original archives and overlays first, and use a new output directory outside the game resources. Other archive entries and HMG padding are retained.

`Asset.CompileSource(path)` is virtual. Each asset reads its source and returns a new asset; the original instance is kept. `AssetCompiler` connects the result to indexed resource paths, updates shared atlases and writes JSON and packed files together.

You can pass an edited `Asset` to `Compile(name, asset)`, or compile several in-memory edits together. `CompileMetadata()` produces updated configuration resources without changing the loaded archive. `SaveTo()` copies entry buffers for writing without compressing and decompressing them first, and publishes the output directory after every archive has been written.

Keep existing workbook sheet names, sprite animation names and indexed atlas texture dimensions. Apply PSD masks and unsupported blend modes before importing. Mesh import accepts static triangle geometry and retains original materials. Lua source is saved as source; this does not compile LuaJIT bytecode. New assets and virtual entries need separate index registration.

### ModPackage

You can save compiled replacements as a portable mod package.

```cs
using EastwardLib.Modding;

var resources = compiler.Compile("script/example.lua", new LuaAsset("return 1"u8.ToArray()));
var mod = ModPackage.Create("Example", assetManager, resources);
mod.SaveTo("example.ewmod");

var restored = ModPackage.Read("example.ewmod");
restored.ApplyTo(assetManager, "./modded");
```

Packages contain changed resources with their original hashes. `Validate()` reports resources from a different game version or another mod before applying replacements. Package reads verify paths, sizes and content hashes. Original archives are kept; applying a mod writes a new output directory.

## Dependencies

The library uses LZ4, Zstd, MessagePack, StbImageSharp and ExcelDataReader. Images use managed RGBA data and do not need System.Drawing or libgdiplus. `HmgAsset.Image` exposes `RgbaImage`; use `HmgAsset.FromImage(path)` to load PNG or JPEG images. FMOD banks are copied without loading the FMOD SDK.

Lua bytecode is kept by default. You can set `AssetManager.LuaDecompiler` to use `LuaAsset.GetSource()` or `SaveSourceTo()`. The native library loads on the first decompilation. Plain Lua source does not load it, and localization parsing does not execute Lua.

### LuaJIT decompiler

You need CMake 3.20+, a C++20 compiler and the [node-luajit-decompiler source](https://github.com/VriskaSerket51/node-luajit-decompiler) to build the optional native library. The decompiler is based on marsinator358's LuaJIT Decompiler v2.

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

Use a checkout with the `decompile(bytes, size)` API; use version 1.1.1 or a compatible version. The bridge does not need Node.js or npm. Outputs are `eastward-luajit.dll`, `libeastward-luajit.so` or `libeastward-luajit.dylib`. Pass an absolute path or place the library beside your application, and include the decompiler's MIT license when distributing it.

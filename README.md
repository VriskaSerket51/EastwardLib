# EastwardLib

C# library for Eastward archives and indexed assets, targeting .NET 10.

```sh
dotnet build -c Release
dotnet run --project tests/EastwardLib.Tests -c Release
```

The regression runner needs no additional test packages. To audit installed resources without starting the game, pass the base content directory followed by any overlay directories:

```sh
dotnet run --project tests/EastwardLib.Tests -c Release -- /path/to/Eastward/content/game /path/to/Eastward/content_dlc
```

## Archives

```cs
using EastwardLib.Assets;

var archive = GArchive.Read("locale.g");
archive["locale/foo"] = new TextAsset("bar").Encode();
archive.Write("locale_new.g");
archive.ExtractTo("./raw"); // raw/locale/...; entries retain their runtime names
```

`GArchive.Create(name)` creates an empty archive. `AddOverlay` merges same-name archives, with later entries taking priority. Rewriting preserves original compression modes, supports UTF-8 names and truncates existing output files. Caller-owned streams can use the `leaveOpen` argument.

## Indexed assets

```cs
using EastwardLib.Utils;

var manager = AssetManager.Create("./content/game/config.g");
manager.RootDirectory = "./content/game";
foreach (var file in Directory.EnumerateFiles(manager.RootDirectory, "*.g"))
{
    manager.LoadArchive(GArchive.Read(file));
}
foreach (var file in Directory.EnumerateFiles("./content_dlc", "*.g"))
{
    manager.LoadArchive(GArchive.Read(file));
}
var scene = manager.LoadAsset("scene/example.scene");
scene?.SaveTo("./output/example.scene");
```

Loaders are registered by type and can be replaced with `RegisterLoader`. Parent/child relationships include virtual deck items, sprite sequences and workbook sheets. `LoadAssets` stops on errors; callers inspecting a complete installation can catch errors per entry, as the audit runner does. Loaded archives and assets are cached in memory. Unload them when no longer needed.

Supported processing includes variable-padding PGF/HMG images, texture atlases with straight-alpha output, packed prototypes, source/compiled scene groups, CSV, OOXML workbooks, SQ source, literal Lua localization tables to PO, Assimp XML to OBJ/MTL, virtual assets and FMOD bank copying. Source scene groups take precedence over stale compiled groups.

`MSpriteAsset.ToAse()` writes animation tags, durations, feature layers, module offsets, origin markers and anchors. `TexturePackAsset.ToPsd()` retains available named texture layers. The writers follow the [Aseprite file specification](https://github.com/aseprite/aseprite/blob/main/docs/ase-file-specs.md) and [Adobe PSD specification](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/). Generated editing files preserve available runtime information; discarded original editing history and layout cannot be recovered.

`ExtractTo` saves assets loaded so far at their logical index paths. Workbooks use `.xlsx`, sprites use `.ase`, and virtual children already represented in a parent file are omitted. Pass `luaSource: true` to request Lua source. An optional error callback allows remaining exports to continue.

## Metadata editing

`AssetIndex`, `ScriptLibrary` and `TextureIndex` retain their original JSON in `Data`.
Unknown fields, tags, dependencies, false/null values, empty tables, numeric/string
versions, texture serializer envelopes, map IDs, unused objects and empty groups
remain available when saving. JSON formatting can change; JSON values and field
types are preserved. Packed numbers retain the precision supported by MessagePack.

`AssetInfo` is now a reference type backed by its entry's JSON. `ObjectFiles` is a
mutable string view; non-string values remain in `Data["objectFiles"]`. The three
indices expose read-only dictionary views so edits cannot silently bypass their
source documents. Use `AssetIndex.Add/Remove/Rename`, `ScriptLibrary.SetModule/RemoveModule`
and `TextureIndex.Update/AddGroup/AddTexture/MoveTexture/RemoveGroup/RemoveTexture`.
New texture groups and textures take complete serialized entries, including the
model and any extra fields; cloning an existing entry retains the engine schema.
Removal detaches texture references without deleting map entries that unknown
objects may still use. Asset subtree renaming changes index keys and parent links;
it does not move runtime resource files or rewrite references inside resource contents.
`FileTree` lives in `EastwardLib.Utils` and is only a presentation utility.

```cs
using System.Text.Json.Nodes;
using EastwardLib.Assets;
using EastwardLib.Utils;

var config = GArchive.Read("config.g");
var manager = AssetManager.Create("config.g");
manager.EditMetadata((assets, scripts, textures) =>
{
    var info = assets["texture/example.png"]; // Replace with an indexed asset path.
    info.Tags ??= new JsonArray();
    info.Tags.Add("modded");
    scripts.SetModule("example", "scripts/example.lua", "script/example");
    textures.Update(textures.Groups[0].Id, group => group["filter"] = "nearest");
});
manager.SaveMetadata(config);
config.Write("output/config.g");
```

`EditMetadata` edits copies and validates all three documents before committing.
A failed edit leaves the documents unchanged. Successful changes rebuild child,
script and texture lookups and invalidate loaded resources. Typed asset setters
and the index editing methods also notify the manager automatically. For direct
edits to `Data`, nested JSON, `Tags` or `Properties`, use `EditMetadata` or call
`RefreshMetadata()` afterwards. Refreshing replaces lookup views; retrieve entries
again rather than retaining old views across a refresh.

Each index provides `ToJson()` and `SaveTo(path)`. `AssetIndex.FromPacked()` and
`Encode(packed: true)` support MessagePack; a `.packed` save path selects that format.
`manager.SaveMetadata(assetPath, scriptPath, texturePath)` writes three files and
rejects colliding paths. `SaveMetadata(archive)` updates the three indices while
retaining other archive entries, and synchronizes an existing `asset_index.packed`.
Packed-only asset indices remain packed-only. When JSON and packed copies coexist,
loading preserves original JSON number spellings and verifies that both describe
the same runtime values; conflicting copies are rejected.

Audit metadata without launching the game:

```sh
dotnet run --project tests/EastwardLib.Tests -c Release -- --metadata /path/to/Eastward/content/game /path/to/Eastward/content_dlc
```

## Source restoration

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/eastward-luajit.dll");
manager.LuaDecompiler = decompiler.Decompile;
var report = new ProjectReconstructor(manager).SaveTo("./restored");
```

`ProjectReconstructor` writes a fresh `game/asset` source tree. It preserves original bytes when available, restores source/compiled scenes, prototypes, SQ, PO, banks and meshes, writes animation-per-file ASE sprite folders with contiguous sub-animation tags, and reconstructs flattened or layered PSD textures and tilesets. FSM, quest, portal and story GraphML writers preserve available topology and metadata. FSM table parsing does not execute Lua. Empty deployed story graphs are reported as unavailable.

Deck PSD packages become Gii raw `.deckpack` folders with geometry, color pixels and normal maps. Original PSD authoring layers are unavailable. References follow changed package prefixes, including workbook cells and Base64 JSON scene prototypes. Virtual importer names retain their original punctuation. Paths escape Windows reserved names and resolve case collisions consistently. Opaque documents and runtime sidecars are preserved.

Source workbooks retain `.xls` paths and use a managed BIFF8/CFB value writer compatible with Gii's public importer. OOXML remains available for decoded export. The XLS writer supports BIFF8's 65,536 rows, 256 columns and 32,767 UTF-16 code units per cell, and a CFB stream up to approximately 7 MiB. It does not invent formulas or formatting removed from the runtime data.

The recovery report is written beside the source tree as `<destination>.recovery.json`. Unavailable resources, JPEG pixel alternatives, path changes and reconstruction provenance are explicit. Existing output directories are rejected. Unexpected conversion failures throw instead of being mislabeled as missing resources.

This restores the asset tree for import into a matching Gii project. Original project settings, host binaries, engine versions and discarded editing history are not reconstructed. Quest transition metadata is retained in GraphML; the public importer needs the compatibility helper in `tests/import_quest_graph.py` to retain that newer field. Full editor loading and engine builds require the original Gii/AKU environment. Source-to-runtime compilation is outside this library.

## Portable dependencies

The default library uses managed LZ4, Zstd, MessagePack and StbImageSharp. PNG encoding uses .NET compression. It does not require System.Drawing, GDI+, libgdiplus or native image codecs. `HmgAsset.Image` now exposes `RgbaImage`; use `HmgAsset.FromImage(path)` for PNG/JPEG input. This replaces the former `System.Drawing.Image` API.

FMOD banks are copied without loading the FMOD SDK. Resource processing does not require FMOD playback bindings.

Lua bytecode is preserved without loading a native decompiler. Configure `AssetManager.LuaDecompiler` with a compatible callback to use `LuaAsset.GetSource()` or `SaveSourceTo()`. `NativeLuaDecompiler` loads the optional library on its first call, accepts byte arrays and releases native result buffers. Plain Lua source does not load it. Localization parsing accepts serialized table literals and rejects executable Lua.

Build the optional C ABI bridge with CMake 3.20+, a C++20 compiler and the [node-luajit-decompiler source](https://github.com/VriskaSerket51/node-luajit-decompiler) (the engine originates from marsinator358's LuaJIT Decompiler v2):

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

No Node.js, npm installation or Node addon is used by the bridge. Build the checkout version matching the byte-array API (`decompile(bytes, size)`); the locally tested checkout is package version 1.1.1. Outputs are `eastward-luajit.dll`, `libeastward-luajit.so` or `libeastward-luajit.dylib`. Pass an absolute path to `NativeLuaDecompiler` or place it beside the application. Distribute the engine's MIT license with its binary. Native Linux/macOS builds need a compiler on those systems; only the Windows native build has been executed locally.

Windows builds and installed-resource conversions are verified locally. Linux/macOS publish checks validate compilation; execution on those systems requires separate verification. Source-to-runtime compilation is not implemented by this library.

## Validation

The local base-game/DLC audit covered 31,790 index entries using 59 registered loaders: 31,037 assets validated, 49 undeployed file/processor resources omitted, and six missing source resources (five empty story graphs and `CreditBH.txt`); no unexpected conversion failures. Fourteen regression scenarios include project paths, reference remapping, embedded prototypes, Unicode XLS continuations and source packages. The Windows C ABI bridge decompiled all 1,577 deployed Lua scripts.

Public Gii validation covers 1,006 source ASE files (111,339 frames, 7,612 tags), restored PSD images/layers, 42 XLS workbooks (125 sheets), 16 quest graphs, 29 portal graphs and 81 FSM graphs. XLS values and metadata match deployed workbooks after re-import; FSM tables match after recompilation. All 678 generated SQ files matched the JS output and round-tripped through Pixpil's public SQ compiler. The previous decoded export also validated 632 combined ASE files and 42 OOXML workbooks.

```sh
python tests/audit_source_project.py /path/to/restored /path/to/gii
dotnet run --project tests/EastwardLib.Tests -c Release -- --fsm /path/to/restored.fsm.json
dotnet run --project tests/EastwardLib.Tests -c Release -- --native /path/to/eastward-luajit.dll /path/to/content/game/script.g
```

The independent Python reader audit requires Pillow, six, PyYAML and Gii's bundled readers. It applies compatibility shims for modern Python without changing Gii or running its application. These checks do not run the game or establish a full editor project build.

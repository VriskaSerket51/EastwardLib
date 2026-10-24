# EastwardLib

English | [한국어](README.ko.md)

Unofficial .NET library for Eastward assets and GArchives.

For CLI tools, see [EastwardPacker](https://github.com/VriskaSerket51/EastwardPacker).

## How to build

.NET 10 required.

```sh
dotnet build -c Release
```

## How to use

Add a file to an archive:

```cs
using EastwardLib.Assets;

var g = GArchive.Read("locale.g");
g["locale/foo"] = new TextAsset("bar").Encode();
g.Write("locale_new.g");
```

Extract assets using the game indexes:

```cs
using EastwardLib.Assets;
using EastwardLib.Utils;

var manager = AssetManager.Create("./content/game/config.g");
manager.RootDirectory = "./content/game";
foreach (var file in Directory.GetFiles(manager.RootDirectory, "*.g"))
{
    manager.RegisterArchive(file);
}
manager.LoadAssets();
manager.ExtractTo("./exported");
```

Register installed DLC archives after the base archives. Use `LoadAsset(path)` for a single asset.

Compile an edited source:

```cs
using EastwardLib.Compilation;

var compiler = new AssetCompiler(manager);
var resources = compiler.Compile("script/example.sq", "./edited/example.sq");
compiler.SaveTo("./compiled", resources);
```

For a whole project, export with `ProjectReconstructor` and compile with `CompileProject()`. Keep `.eastward-project.json` for path mappings. Data missing from the game cannot be recovered.

Use [EastwardModTool](https://github.com/VriskaSerket51/EastwardModTool) to edit assets and create `.ewmod` packages through a GUI.

## Lua source export

Requires CMake 3.20+, a C++20 compiler and [node-luajit-decompiler](https://github.com/VriskaSerket51/node-luajit-decompiler).

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

Pass the built library to the asset manager:

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/native-library");
manager.LuaDecompiler = decompiler.Decompile;
```

Decompiled code may differ from the original source.

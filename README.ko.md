# EastwardLib

[English](README.md) | 한국어

Eastward 에셋과 GArchive를 다루는 비공식 .NET 라이브러리입니다.

CLI 도구로는 [EastwardPacker](https://github.com/VriskaSerket51/EastwardPacker)가 있습니다.

## 빌드 방법

.NET 10이 필요합니다.

```sh
dotnet build -c Release
```

## 사용 방법

아카이브에 파일 추가:

```cs
using EastwardLib.Assets;

var g = GArchive.Read("locale.g");
g["locale/foo"] = new TextAsset("bar").Encode();
g.Write("locale_new.g");
```

게임 인덱스로 에셋 추출:

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

설치된 DLC는 기본 아카이브 뒤에 등록합니다. 에셋 하나만 읽으려면 `LoadAsset(path)`를 사용하면 됩니다.

수정한 소스 컴파일:

```cs
using EastwardLib.Compilation;

var compiler = new AssetCompiler(manager);
var resources = compiler.Compile("script/example.sq", "./edited/example.sq");
compiler.SaveTo("./compiled", resources);
```

프로젝트 전체는 `ProjectReconstructor`로 추출한 뒤 `CompileProject()`로 컴파일합니다.

> 경로 매핑용 `.eastward-project.json`은 함께 보관해야 합니다.
> 게임에 없는 정보는 복구할 수 없습니다.

[EastwardModTool](https://github.com/VriskaSerket51/EastwardModTool)에서 GUI로 편집하거나 `.ewmod`를 만들 수 있습니다.

## Lua 소스 추출

CMake 3.20 이상, C++20 컴파일러, [node-luajit-decompiler](https://github.com/VriskaSerket51/node-luajit-decompiler)가 필요합니다.

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

빌드한 라이브러리는 아래처럼 연결합니다.

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/native-library");
manager.LuaDecompiler = decompiler.Decompile;
```

디컴파일 결과는 원본 소스와 다를 수 있습니다.

# EastwardLib

[English](README.md) | 한국어

Eastward 에셋과 GArchive를 다루는 비공식 .NET 라이브러리입니다.

CLI 도구는 [EastwardPacker](https://github.com/VriskaSerket51/EastwardPacker)를 참고하세요.

## 빌드 방법

.NET 10이 필요합니다.

```sh
dotnet build -c Release
```

## 사용 방법

### GArchive

아카이브에 파일을 추가하는 예제입니다.

```cs
using EastwardLib.Assets;

var g = GArchive.Read("locale.g");
g["locale/foo"] = new TextAsset("bar").Encode();
g.Write("locale_new.g");
```

파일을 추출하는 예제입니다.

```cs
var g = GArchive.Read("locale.g");
g.ExtractTo("./locale_extracted");
```

### AssetManager

인덱스에 기록된 이름과 경로로 에셋을 추출할 수 있습니다.

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

설치된 DLC 아카이브를 함께 읽으려면 기본 아카이브 뒤에 등록하세요. `LoadAsset(path)`로 에셋 하나만 읽을 수도 있습니다.

### 메타데이터

`EditMetadata()`로 에셋, 스크립트, 텍스처 인덱스를 수정하고 `SaveMetadata()`로 저장할 수 있습니다.

```cs
var config = GArchive.Read("config.g");
assetManager.EditMetadata((assets, scripts, textures) =>
{
    scripts.SetModule("example", "scripts/example.lua", "script/example");
});
assetManager.SaveMetadata(config);
config.Write("config_new.g");
```

인덱스의 이름을 바꿔도 리소스 파일 안의 참조는 그대로 남습니다.

### 편집용 파일 내보내기와 컴파일

게임의 에셋과 인덱스에서 편집용 파일을 만드는 예제입니다.

```cs
new ProjectReconstructor(assetManager).SaveTo("./restored");
```

파일은 `game/asset`에 저장됩니다. 배포본에 없는 원본 프로젝트 설정과 편집 정보는 복원하지 못합니다.

수정한 소스를 아카이브에 반영하는 예제입니다.

```cs
using EastwardLib.Compilation;

var compiler = new AssetCompiler(assetManager);
var resources = compiler.Compile("script/example.sq", "./edited/example.sq");
compiler.SaveTo("./compiled", resources);
```

내보낸 파일 트리 전체는 `CompileProject()`를 사용합니다. 프로젝트 루트의 `.eastward-project.json`은 재컴파일에 필요한 경로 매핑이므로 함께 보관하세요. 결과는 새 디렉터리에 출력합니다.

### ModPackage

컴파일한 변경분을 `.ewmod`로 묶을 수 있습니다.

```cs
using EastwardLib.Modding;

var mod = ModPackage.Create("Example", assetManager, resources);
mod.SaveTo("example.ewmod");
```

`Read()`와 `Validate()`로 패키지와 원본 리소스를 확인합니다. `ApplyTo()`는 새 `.g`를 출력합니다. `.ewmod` 직접 설치는 EastwardModLoader를 사용합니다.

## LuaJIT 디컴파일러

LuaJIT 바이트코드를 Lua 코드로 변환할 때는 디컴파일러를 연결하세요. 결과는 원본 소스와 다를 수 있습니다.

```cs
using var decompiler = new NativeLuaDecompiler("/path/to/native-library");
assetManager.LuaDecompiler = decompiler.Decompile;
```

CMake 3.20 이상, C++20 컴파일러와 [디컴파일러 소스](https://github.com/VriskaSerket51/node-luajit-decompiler)가 필요합니다.

```sh
cmake -S Native -B build/native -DLUAJIT_DECOMPILER_SOURCE=/absolute/path/to/node-luajit-decompiler
cmake --build build/native --config Release
cmake --install build/native --config Release --prefix /path/to/native-output
```

빌드한 라이브러리 경로를 `NativeLuaDecompiler`에 지정합니다.

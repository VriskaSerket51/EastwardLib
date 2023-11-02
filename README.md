# EastwardLib
By importing "EastwardLib.dll", you can handle Assets and GArchives.

For example,
```cs
using EastwardLib;
using EastwardLib.MetaData;

AssetIndex.Create("asset_index");
var g = GArchive.Read("locale.g");
g["foo"] = new TextAsset("bar");
g.Write("locale_new.g");
```

# EastwardLib
By importing "EastwardLib.dll", you can handle Assets and GArchives.

For example,
```cs
using EastwardLib;
using EastwardLib.MetaData;

var g = GArchive.Read("locale.g");
g["foo"] = new TextAsset("bar");
g.Write("locale_new.g");
```

Currently, you cannot export lua, audio and some textures.

using EastwardLib.Assets;
using SimpleJSON;

namespace EastwardLib.MetaData;

public class AssetIndex : Dictionary<string, AssetInfo>
{
    private AssetIndex(int capacity = 100) : base(capacity)
    {
    }

    public static AssetIndex Create(string json)
    {
        var root = JSONNode.Parse(json);
        AssetIndex assetIndex = new AssetIndex(root.Count);

        foreach (var (key, value) in root)
        {
            string assetName = key;
            string? filePath = value["filePath"].IsBoolean ? null : value["filePath"].Value;
            string fileType = value["fileType"].Value;
            string? groupType = value["groupType"].IsNull ? null : value["groupType"].Value;

            var objectFilesNode = value["objectFiles"];
            var objectFiles = new Dictionary<string, string>(objectFilesNode.Count);
            foreach (var (t, v) in objectFilesNode)
            {
                objectFiles.Add(t, v.Value);
            }

            string type = value["type"].Value;

            var assetInfo = new AssetInfo(assetName, filePath, fileType, groupType, objectFiles, type);
            assetIndex.Add(assetName, assetInfo);
        }

        return assetIndex;
    }

    public AssetInfo? SearchAssetInfo(string key)
    {
        if (!ContainsKey(key))
        {
            return null;
        }

        return this[key];
    }

    public AssetInfo? GetAssetInfoByName(string name)
    {
        foreach (var (_, value) in this)
        {
            if (value.AssetName == name)
            {
                return value;
            }
        }

        return null;
    }

    public void ExtractArchive(GArchive archive, string path)
    {
        // var archiveName = archive.ArchiveName;
        // AssetIndex assetIndex = this;
        //
        // foreach (var (fullName, data) in archive)
        // {
        //     bool isPackage = false;
        //     var realName = fullName;
        //     var assetName = fullName.Substring(archiveName.Length + 1);
        //     if (assetName.Contains('/'))
        //     {
        //         isPackage = true;
        //         var split = assetName.Split('/');
        //         realName = $"{archiveName}/{split[0]}";
        //     }
        //
        //     if (!TryGetValue(realName, out var assetInfo))
        //     {
        //         Console.WriteLine($"[WARN]: Cannot find AssetInfo of {fullName}");
        //         continue;
        //     }
        //
        //     var asset = Asset.Create(fullName, data, assetInfo, isPackage);
        // }

        // foreach (var (name, data) in archive)
        // {
        //     if (data is FallbackAsset)
        //     {
        //         if (!string.IsNullOrEmpty(fallbackPath))
        //         {
        //             data.SaveTo(Path.Combine(fallbackPath, name));
        //         }
        //
        //         continue;
        //     }
        //
        //     string originName = name.Substring(archiveName.Length + 1);
        //     bool isPackage = originName.Contains('/');
        //
        //     if (isPackage)
        //     {
        //         var split = originName.Split('/');
        //         string containerName = $"{archiveName}/{split[0]}";
        //         AssetInfo? assetInfo = assetIndex.SearchAssetInfo(containerName);
        //         if (assetInfo == null)
        //         {
        //             Console.WriteLine($"{containerName} has null asset info");
        //             continue;
        //         }
        //
        //         if (assetInfo.Value.FileType == "f")
        //         {
        //             // WTF?!
        //             continue;
        //         }
        //
        //         data.SaveTo(Path.Combine(path, assetInfo.Value.FilePath,
        //             originName.Substring(split[0].Length + 1)));
        //     }
        //     else
        //     {
        //         AssetInfo? assetInfo = assetIndex.SearchAssetInfo(name);
        //         if (assetInfo == null)
        //         {
        //             Console.WriteLine($"{name} has null asset info");
        //             continue;
        //         }
        //
        //         string assetName = assetInfo.Value.AssetName;
        //         string filePath = assetInfo.Value.FilePath;
        //         string fileType = assetInfo.Value.FileType;
        //         var objectFiles = assetInfo.Value.ObjectFiles;
        //         string type = assetInfo.Value.Type;
        //
        //         if (fileType == "d")
        //         {
        //             string key = objectFiles.First(o => o.Value == name).Key;
        //             data.SaveTo(Path.Combine(path, filePath, key));
        //         }
        //         else if (fileType == "v")
        //         {
        //             if (type == "texture")
        //             {
        //                 string mSpriteName = $"{assetName.Substring(0, assetName.Length - 8)}.msprite";
        //                 var mSpriteInfo = assetIndex.GetAssetInfoByName(mSpriteName);
        //                 if (mSpriteInfo == null)
        //                 {
        //                     continue;
        //                 }
        //
        //                 Console.WriteLine(mSpriteInfo.Value.FilePath);
        //
        //                 data.SaveTo(Path.Combine(path, Path.GetDirectoryName(mSpriteInfo.Value.FilePath)!,
        //                     assetName + ".png"));
        //             }
        //         }
        //         else if (fileType == "f")
        //         {
        //             data.SaveTo(Path.Combine(path, filePath));
        //         }
        //     }
        // }
    }
}
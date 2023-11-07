using SimpleJSON;

namespace EastwardLib.MetaData;

public struct AssetInfo
{
    public string AssetName;
    public string? FilePath;
    public string FileType;
    public string? GroupType;
    public readonly Dictionary<string, string> ObjectFiles;
    public string Type;

    public AssetInfo(string assetName, string? filePath, string fileType, string? groupType, Dictionary<string, string> objectFiles, string type)
    {
        AssetName = assetName;
        FilePath = filePath;
        FileType = fileType;
        GroupType = groupType;
        ObjectFiles = objectFiles;
        Type = type;
    }
}
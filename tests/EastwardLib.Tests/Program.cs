using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Assets;
using EastwardLib.Compilation;
using EastwardLib.MetaData;
using EastwardLib.Modding;
using EastwardLib.Utils;
using MessagePack;

var tests = new (string Name, Action Run)[]
{
    ("UTF-8 archive names, empty files, overwrite and embedded streams", Archive),
    ("HMG pixels, padding, PNG and transparent RGB", Image),
    ("Literal Lua escapes, comments and code rejection", Lua),
    ("Source scene groups take priority over stale compiled groups", Scene),
    ("Workbook localization headers and vertical sheets", Workbook),
    ("Mesh world transforms, normals and reflection winding", Mesh),
    ("Virtual sprite sequences and loader registration", VirtualAssets),
    ("ASE tags, cel pixels and PSD layers", SourceImages),
    ("Lua decompilation is lazy and caller streams remain open", LazyLua),
    ("Generated FSM parsing and GraphML structure", Graphs),
    ("Project paths, reference boundaries and embedded prototypes", ProjectReferences),
    ("BIFF8 Unicode strings and source animation tags", SourcePackages),
    ("Source project packages and original references", SourceProject),
    ("Extraction containment", Paths),
    ("Shared output paths, concurrent writes, PNG, archives and locale encoding", Output),
    ("Lossless metadata editing, validation, relationships and cache invalidation", Metadata),
    ("Source compilation, shared atlases and archive preservation", Compilation),
    ("In-memory edits, mod packages, compatibility and transactional saves", Modding),
    ("Lazy archive loading and transactional overlay resolution", LazyArchives)
};
foreach (var test in tests) { test.Run(); Console.WriteLine("PASS " + test.Name); }

if (args.Length >= 3 && args[0] == "--compile")
{
    AuditCompilation(args[1], args.Skip(2).ToArray());
    return;
}
if (args.Length > 0 && args[0] == "--metadata")
{
    var count = 0;
    foreach (var directory in args.Skip(1))
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*config*.g"))
        {
            var archive = GArchive.Read(path);
            var prefix = archive.ArchiveName + "/";
            if (!archive.ContainsKey(prefix + "asset_index"))
            {
                continue;
            }
            var assets = Encoding.UTF8.GetString(archive[prefix + "asset_index"]);
            var scripts = Encoding.UTF8.GetString(archive[prefix + "script_library"]);
            var textures = Encoding.UTF8.GetString(archive[prefix + "texture_index"]);
            var index = AssetIndex.Create(assets);
            var library = ScriptLibrary.Create(scripts);
            var textureIndex = TextureIndex.Create(textures);
            Check(JsonNode.DeepEquals(index.Data, AssetManager.Create(path).Index.Data), "Archive loading changed original JSON metadata");
            Check(JsonNode.DeepEquals(JsonNode.Parse(assets), JsonNode.Parse(index.ToJson())), "Installed asset metadata was changed");
            Check(JsonNode.DeepEquals(JsonNode.Parse(scripts), JsonNode.Parse(library.ToJson())), "Installed script metadata was changed");
            Check(JsonNode.DeepEquals(JsonNode.Parse(textures), JsonNode.Parse(textureIndex.ToJson())), "Installed texture metadata was changed");
            if (archive.TryGetValue(prefix + "asset_index.packed", out var originalPacked))
            {
                var packedIndex = AssetIndex.FromPacked(originalPacked);
                Check(JsonNode.DeepEquals(packedIndex.Data, AssetIndex.FromPacked(packedIndex.Encode(true)).Data), "Installed packed index round trip differs");
                Check(JsonNode.DeepEquals(AssetIndex.FromPacked(index.Encode(true)).Data, packedIndex.Data), "Installed JSON and packed runtime values differ");
            }
            var manager = new AssetManager(index, library, textureIndex);
            manager.EditMetadata((entries, _, _) => entries.Values.First().Data["roundTripFixture"] = new JsonObject { ["value"] = "한글", ["enabled"] = false });
            manager.SaveMetadata(archive);
            using var stream = new MemoryStream();
            archive.Write(stream, true);
            var restored = GArchive.Read(archive.ArchiveName, stream.ToArray());
            var rewritten = AssetIndex.Create(Encoding.UTF8.GetString(restored[prefix + "asset_index"]));
            Check(rewritten.Values.First().Data["roundTripFixture"]!["value"]!.ToString() == "한글", "Installed metadata edit did not survive archive writing");
            if (restored.TryGetValue(prefix + "asset_index.packed", out var savedPacked))
            {
                Check(JsonNode.DeepEquals(AssetIndex.FromPacked(rewritten.Encode(true)).Data, AssetIndex.FromPacked(savedPacked).Data), "Packed runtime metadata was not updated with JSON");
            }
            count += index.Count;
            Console.WriteLine($"PASS {path}: {index.Count} assets, {library.Count} scripts, {textureIndex.Textures.Count} textures, {textureIndex.Groups.Count} groups");
        }
    }
    Check(count > 0, "No installed metadata found");
    Console.WriteLine($"PASS {count} installed metadata entries preserved and edited through archive round trips");
}
else if (args.Length > 0 && args[0] == "--hmg")
{
    var count = 0;
    var saved = false;
    foreach (var directory in args.Skip(1))
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.g"))
        {
            var archive = GArchive.Read(path);
            foreach (var (name, bytes) in archive.Where(pair => pair.Value.AsSpan().StartsWith("PGF"u8)))
            {
                var image = Asset.Create<HmgAsset>(bytes);
                var encoded = image.Encode();
                var restored = Asset.Create<HmgAsset>(encoded);
                Check(restored.Image!.Pixels.SequenceEqual(image.Image!.Pixels), "Installed HMG round trip differs: " + name);
                count++;
                if (!saved && Environment.GetEnvironmentVariable("EASTWARD_TEST_HMG") is string output)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "original.hmg"), bytes);
                    File.WriteAllBytes(Path.Combine(output, "csharp.hmg"), encoded);
                    File.WriteAllBytes(Path.Combine(output, "pixels.rgba"), image.Image.Pixels);
                    saved = true;
                }
            }
        }
    }
    Check(count > 0, "No installed HMG resources found");
    Console.WriteLine($"PASS {count} installed HMG decode/re-encode pixel round trips");
}
else if (args.Length > 0 && args[0] == "--hmg-file")
{
    var image = Asset.Create<HmgAsset>(File.ReadAllBytes(args[1]));
    Check(image.Image!.Pixels.SequenceEqual(File.ReadAllBytes(args[2])), "JS/C# HMG pixels differ");
    Console.WriteLine("PASS JS-encoded HMG decoded by C#");
}
else if (args.Length > 0 && args[0] == "--fsm")
{
    var comparisons = JsonNode.Parse(File.ReadAllText(args[1]))!.AsArray();
    foreach (var item in comparisons)
    {
        Check(JsonNode.DeepEquals(FsmSource.Parse(item!["original"]!.ToString()), FsmSource.Parse(item["recompiled"]!.ToString())), "FSM effective tables differ: " + item["path"]);
    }

    Console.WriteLine($"PASS {comparisons.Count} public Gii FSM compiler round trips");
}
else if (args.Length > 0 && args[0] == "--native")
{
    Native(args[1], args.Length > 2 ? args[2] : null);
}
else if (args.Length > 0)
{
    Audit(args);
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void Compilation()
{
    var root = Path.Combine(Path.GetTempPath(), "EastwardLib-compile-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        var manager = Manager("""
            {"hello.txt":{"type":"text","objectFiles":{"data":"fixture/text"}},"event.sq":{"type":"sq_script","objectFiles":{"data":"fixture/sq","packed_data":"fixture/sq.packed"}},"scene.proto":{"type":"proto","objectFiles":{"def":"fixture/proto","packed_def":"fixture/proto.packed"}}}
            """);
        var archive = GArchive.Create("fixture");
        archive["fixture/text"] = "original"u8.ToArray();
        archive["fixture/sq"] = Encoding.UTF8.GetBytes(SQCompiler.Compile("", "event.sq").ToJsonString());
        archive["fixture/proto"] = "{}"u8.ToArray();
        archive["fixture/untouched"] = [1, 3, 5];
        manager.LoadArchive(archive);
        var text = Path.Combine(root, "hello.txt");
        File.WriteAllText(text, "edited 한글");
        Asset originalText = manager.LoadAsset("hello.txt")!;
        var editedText = originalText.CompileSource(text);
        Check(editedText is TextAsset && Encoding.UTF8.GetString(editedText.Encode()) == "edited 한글" && Encoding.UTF8.GetString(originalText.Encode()) == "original", "Virtual source compilation changed its original asset");
        var sq = Path.Combine(root, "event.sq");
        File.WriteAllText(sq, "!start\n@John,Sam\n\tif ready\n\t\tsay: 안녕 //$voice(hello)\n\t\t\tsecond line\n\telse\n\t\twait 1; go\n");
        var compiler = new AssetCompiler(manager);
        var result = compiler.Compile(new Dictionary<string, string> { ["hello.txt"] = text, ["event.sq"] = sq });
        Check(Encoding.UTF8.GetString(result["fixture/text"]) == "edited 한글" && JsonNode.DeepEquals(JsonNode.Parse(result["fixture/sq"]), JsonNode.Parse(MessagePackSerializer.ConvertToJson(result["fixture/sq.packed"]))), "Compiled JSON and packed SQ differ");
        var data = JsonNode.Parse(result["fixture/sq"])!;
        var action = data["children"]![1]!["children"]![0]!["children"]![0]!;
        Check(action["args"]![1]!.ToString() == "second line" && action["inlineDirectives"]![0]!["name"]!.ToString() == "voice", "Multiline SQ or directives lost");
        var output = Path.Combine(root, "archives");
        compiler.SaveTo(output, result);
        var saved = GArchive.Read(Path.Combine(output, "fixture.g"));
        Check(saved["fixture/untouched"].SequenceEqual(archive["fixture/untouched"]) && Encoding.UTF8.GetString(archive["fixture/text"]) == "original", "Compilation changed unrelated files or the loaded archive");
        File.WriteAllText(sq, "else");
        Reject(() => compiler.Compile(new Dictionary<string, string> { ["hello.txt"] = text, ["event.sq"] = sq }));
        Check(Encoding.UTF8.GetString(archive["fixture/text"]) == "original", "Failed compilation changed the archive");
        Reject(() => SQCompiler.Compile("switch value\n\twait 1"));
        Reject(() => SQCompiler.Compile(".name value"));
        var proto = Path.Combine(root, "source.proto");
        File.WriteAllText(proto, "{\"unknown\":false}");
        result = compiler.Compile("scene.proto", proto);
        Check(JsonNode.Parse(MessagePackSerializer.ConvertToJson(result["fixture/proto.packed"]))!["unknown"]!.ToJsonString() == "false", "Prototype compilation lost values");

        var image = new RgbaImage(1, 1, [50, 60, 70, 128]);
        var composite = Path.Combine(root, "composite.psd");
        File.WriteAllBytes(composite, PsdWriter.Write(image));
        Asset originalImage = new HmgAsset(new RgbaImage(1, 1, [1, 2, 3, 4]));
        var editedImage = (HmgAsset)originalImage.CompileSource(composite);
        Check(editedImage.Image!.Pixels.SequenceEqual(image.Pixels) && ((HmgAsset)originalImage).Image!.Pixels[0] == 1, "Composite PSD source import changed pixels or its original image");
        var compositeBytes = File.ReadAllBytes(composite);
        foreach (var prediction in new[] { false, true })
        {
            using var compressed = new MemoryStream();
            compressed.Write(compositeBytes.AsSpan(0, 38));
            compressed.WriteByte(0);
            compressed.WriteByte((byte)(prediction ? 3 : 2));
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Compress, true)) zlib.Write(compositeBytes.AsSpan(40));
            File.WriteAllBytes(composite, compressed.ToArray());
            Check(PsdReader.LoadImage(composite).Pixels.SequenceEqual(image.Pixels), "ZIP PSD composite lost pixels");
        }
        var psd = Path.Combine(root, "layers.psd");
        File.WriteAllBytes(psd, PsdWriter.Write([new PsdWriter.Layer("그룹", Children: [new PsdWriter.Layer("pixel", image, 2, 3)])]));
        var layers = PsdReader.Read(psd);
        Check(layers[0].Name == "그룹" && layers[0].Children![0].X == 2 && layers[0].Children![0].Image!.Pixels.SequenceEqual(image.Pixels), "PSD layer import changed names, offsets or pixels");
        var definition = JsonNode.Parse("""
            {"anims":{"a":{"name":"idle","seq":[["f",0.1]]}},"frames":{"f":{"parts":[["m",-2,3]],"meta":{"anchors":{"hand":[[1,2,"#ff0000"]]}}}},"modules":{"m":{"rect":[0,0,1,1],"atlas":"a","feature":1}},"features":[{"id":1,"name":"head.eye"}]}
            """)!;
        var ase = Path.Combine(root, "sprite.ase");
        File.WriteAllBytes(ase, AseWriter.Write(definition, new Dictionary<string, RgbaImage> { ["a"] = image }));
        var sprite = SpriteCompiler.Compile(ase, definition, "a", "atlas", 16, 16);
        var spriteData = sprite.Definition;
        Check(spriteData["anims"]!["1"]!["name"]!.ToString() == "idle" && spriteData["frames"]!["1"]!["parts"]![0]![1]!.GetValue<int>() == -2 && spriteData["frames"]!["1"]!["meta"]!["anchors"]!["hand"]![0]![1]!.GetValue<int>() == 2, "ASE compilation lost animation names, origin or anchors");
        Check(AseReader.Read(ase).Frames[0].Duration == 100 && AseWriter.Write(spriteData, new Dictionary<string, RgbaImage> { ["a"] = sprite.Atlas }).Length > 128, "Compiled sprite cannot be read again");

        var workbook = new WorkbookAsset(JsonNode.Parse("{\"table\":[{\"name\":\"Sam\",\"hp\":10}],\"vertical\":[{\"x\":1},{\"x\":2}]}")!, JsonNode.Parse("{\"table\":{\"type\":\"list\",\"keys\":[\"name\",\"hp\"],\"keyMeta\":[\"localizable\",false],\"unknown\":false},\"vertical\":{\"type\":\"vlist\",\"keys\":[\"x\"],\"keyMeta\":[false]}}")!);
        foreach (var extension in new[] { ".xls", ".xlsx" })
        {
            var file = Path.Combine(root, "book" + extension);
            workbook.SaveTo(file);
            var book = WorkbookCompiler.Compile(file, workbook.Metadata);
            Check(JsonNode.DeepEquals(book.Data, workbook.Data) && book.Metadata["table"]!["keyMeta"]![0]!.ToString() == "localizable" && book.Metadata["table"]!["unknown"]!.ToJsonString() == "false", "Workbook source compilation changed values or metadata");
        }
        var po = LocaleCompiler.ReadPo("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\nmsgid \"hello\"\nmsgstr \"안녕\\n\"\n\"second\"\n");
        Check(po["hello"]!.ToString() == "안녕\nsecond" && JsonNode.DeepEquals(LuaTable.Parse("return " + LocaleCompiler.WriteLua(po)), po), "PO compilation or Lua escaping differs");
        Check(CsvCompiler.Compile("name,value\n\"hello, world\",1\n", "test.list.csv")[0]!["name"]!.ToString() == "hello, world", "CSV quoting changed");
        Reject(() => CsvCompiler.Compile("\"unterminated", "test.csv"));

        var fsm = "return (function() nodelist={['start']={localName='start',type='state'};['done']={localName='done',type='state'};}; nodelist['start'].next={['$done']='done';[true]='done';}; return nodelist end) ()";
        var fsmCompiled = FsmSource.Parse(GraphCompiler.Compile("fsm_scheme", SourceGraph.FromFsm(fsm), fsm));
        Check(fsmCompiled["start"]!["next"]!["true"]!.ToString() == "done", "FSM fallback compilation differs");
        var quest = JsonNode.Parse("{\"nodes\":[{\"id\":\"a\",\"name\":\"first\",\"transition\":{\"flag\":1}},{\"id\":\"b\",\"name\":\"next\"}],\"connections\":[{\"from\":\"a\",\"to\":\"b\",\"type\":\"spawn\",\"cond\":\"ready\"}]}")!;
        var questCompiled = JsonNode.Parse(GraphCompiler.Compile("quest_scheme", SourceGraph.Write("quest_scheme", quest), quest.ToJsonString()))!;
        Check(questCompiled["nodes"]![0]!["transition"]!["flag"]!.GetValue<int>() == 1 && questCompiled["connections"]![0]!["type"]!.ToString() == "spawn", "Quest transition compilation differs");
        var mesh = "<ASSIMP><Scene><Node><MeshRefs>0</MeshRefs></Node><MeshList><Mesh><Positions>0 0 0\n1 0 0\n0 1 0</Positions><Normals>0 0 1\n0 0 1\n0 0 1</Normals><FaceList><Face>0 1 2</Face></FaceList></Mesh></MeshList></Scene></ASSIMP>";
        var converted = MeshConverter.Convert(mesh);
        Check(MeshConverter.Convert(MeshCompiler.Compile(converted.Obj, mesh)).Obj == converted.Obj, "OBJ compilation changed geometry");

        var textureData = TextureIndex.Create("""
            {"root":"1","map":{"1":{"body":{"groups":["2"]}},"2":{"body":{"atlasMode":true,"atlasCachePath":"fixture/atlas","premultiplyAlpha":true,"textures":["3","4"]}},"3":{"body":{"path":"a.png","atlasId":1,"x":1,"y":1,"w":1,"h":1}},"4":{"body":{"path":"b.png","atlasId":1,"x":2,"y":1,"w":1,"h":1}}}}
            """);
        var textureManager = new AssetManager(AssetIndex.Create("{\"a.png\":{\"type\":\"texture\"},\"b.png\":{\"type\":\"texture\"}}"), ScriptLibrary.Create("{\"source\":{}}"), textureData);
        var originalPage = new HmgAsset(new RgbaImage(4, 4, Enumerable.Repeat((byte)255, 64).ToArray()));
        archive["fixture/atlas/page.hmg"] = originalPage.Encode([1, 2], [3]);
        archive["fixture/atlas/atlas.json"] = "{\"atlases\":[{\"name\":\"page.hmg\"}]}"u8.ToArray();
        textureManager.LoadArchive(archive);
        var a = Path.Combine(root, "a.png");
        var b = Path.Combine(root, "b.png");
        image.SavePng(a);
        new RgbaImage(1, 1, [10, 20, 30, 255]).SavePng(b);
        var pages = new AssetCompiler(textureManager).Compile(new Dictionary<string, string> { ["a.png"] = a, ["b.png"] = b });
        var page = Asset.Create<HmgAsset>(pages["fixture/atlas/page.hmg"]);
        Check(page.Image!.Crop(1, 1, 1, 1).Pixels.SequenceEqual(new byte[] { 25, 30, 35, 128 }) && page.Image.Crop(2, 1, 1, 1).Pixels.SequenceEqual(new byte[] { 10, 20, 30, 255 }) && page.Image.Crop(0, 0, 1, 1).Pixels.All(value => value == 255), "Shared atlas edit lost replacement pixels or changed its neighbours");
        Check(page.Header!.PaddingA.Span.SequenceEqual(new byte[] { 1, 2 }) && page.Header.PaddingB.Span.SequenceEqual(new byte[] { 3 }), "Atlas compilation changed HMG padding");
    }
    finally { Directory.Delete(root, true); }
}

static void LazyArchives()
{
    var root = Path.Combine(Path.GetTempPath(), "EastwardLib-lazy-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        var manager = Manager("{}");
        var original = GArchive.Create("fixture");
        original["fixture/text"] = "base"u8.ToArray();
        var basePath = Path.Combine(root, "base", "fixture.g");
        original.Write(basePath);
        manager.RegisterArchive(basePath);
        File.WriteAllBytes(basePath, []);
        Reject(() => manager.LoadFile("fixture/text"));
        original.Write(basePath);
        var overlay = GArchive.Create("fixture");
        overlay["fixture/text"] = "overlay"u8.ToArray();
        overlay["fixture/new"] = [7];
        var overlayPath = Path.Combine(root, "overlay", "fixture.g");
        overlay.Write(overlayPath);
        manager.RegisterArchive(overlayPath);
        Check(Encoding.UTF8.GetString(manager.LoadFile("fixture/text")!) == "overlay" && manager.FileExists("fixture/new") && manager.GetFiles("fixture").Count == 2, "Lazy overlays did not retain later entries");
        manager.UnloadArchives();
        manager.LoadArchive(original);
        manager.RegisterArchive(overlayPath);
        var corruptPath = Path.Combine(root, "corrupt", "fixture.g");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllBytes(corruptPath, []);
        manager.RegisterArchive(corruptPath);
        Reject(() => manager.LoadFile("fixture/text"));
        Check(Encoding.UTF8.GetString(original["fixture/text"]) == "base" && !original.ContainsKey("fixture/new"), "Failed pending overlays partially changed a loaded archive");
        overlay.Write(corruptPath);
        Check(Encoding.UTF8.GetString(manager.LoadFile("fixture/text")!) == "overlay", "Failed lazy archive resolution could not be retried");
        manager.UnloadArchive("fixture");
        Check(manager.LoadFile("fixture/text") == null, "Unloaded archive remains available");
        manager.RegisterArchive(basePath);
        manager.UnloadArchives();
        Check(manager.LoadFile("fixture/text") == null, "Unloaded registrations remain available");
    }
    finally { Directory.Delete(root, true); }
}

static void Modding()
{
    var root = Path.Combine(Path.GetTempPath(), "EastwardLib-mod-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        var manager = Manager("{\"hello.txt\":{\"type\":\"text\",\"objectFiles\":{\"data\":\"fixture/text\"}}}");
        var original = GArchive.Create("fixture");
        original["fixture/text"] = "original"u8.ToArray();
        original["fixture/unchanged"] = [1, 2, 3];
        manager.LoadArchive(original);
        var config = GArchive.Create("config");
        manager.SaveMetadata(config);
        config["config/asset_index.packed"] = manager.Index.Encode(true);
        config["config/extra"] = [8, 9];
        manager.LoadArchive(config);
        var configBefore = config.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone());
        var compiler = new AssetCompiler(manager);
        var resources = new Dictionary<string, byte[]>(compiler.Compile("hello.txt", new TextAsset("edited")));
        Check(Encoding.UTF8.GetString(((TextAsset)manager.LoadAsset("hello.txt")!).Encode()) == "original", "In-memory compilation changed the original asset");
        Reject(() => compiler.Compile(new[] { new KeyValuePair<string, Asset>("hello.txt", new TextAsset("a")), new KeyValuePair<string, Asset>("hello.txt", new TextAsset("b")) }));
        manager.EditMetadata((assets, _, _) => assets["hello.txt"].Tags = new JsonArray("modded"));
        foreach (var (path, bytes) in compiler.CompileMetadata()) resources.Add(path, bytes);
        Check(configBefore.All(pair => config[pair.Key].SequenceEqual(pair.Value)), "Compiling metadata changed the original config archive");
        resources.Add("fixture/unchanged", [1, 2, 3]);
        var mod = ModPackage.Create("Example", manager, resources);
        Check(!mod.ResourcePaths.Contains("fixture/unchanged"), "Mod package contains unchanged data");
        resources["fixture/text"][0] = 0;
        var detached = mod.GetResource("fixture/text");
        detached[0] = 0;
        Check(Encoding.UTF8.GetString(mod.GetResource("fixture/text")) == "edited", "Mod resource buffers are not isolated");
        var pathToMod = Path.Combine(root, "example.ewmod");
        mod.SaveTo(pathToMod);
        var restored = ModPackage.Read(pathToMod);
        Check(restored.Name == "Example" && restored.Validate(manager).Count == 0, "Mod package did not round-trip");
        var output = Path.Combine(root, "applied");
        restored.ApplyTo(manager, output);
        var saved = GArchive.Read(Path.Combine(output, "fixture.g"));
        Check(Encoding.UTF8.GetString(saved["fixture/text"]) == "edited" && saved["fixture/unchanged"].SequenceEqual(original["fixture/unchanged"]), "Applying a mod lost original archive entries");
        var savedConfig = GArchive.Read(Path.Combine(output, "config.g"));
        Check(savedConfig["config/extra"].SequenceEqual(config["config/extra"]) && JsonNode.DeepEquals(JsonNode.Parse(savedConfig["config/asset_index"]), JsonNode.Parse(MessagePackSerializer.ConvertToJson(savedConfig["config/asset_index.packed"]))), "Applying metadata lost config entries or packed synchronization");
        original["fixture/text"] = "another version"u8.ToArray();
        var conflicts = restored.Validate(manager);
        Check(conflicts.SequenceEqual(new[] { "fixture/text" }), "Mod compatibility failed to detect a changed original");
        var rejected = Path.Combine(root, "rejected");
        Reject(() => restored.ApplyTo(manager, rejected));
        Check(!Directory.Exists(rejected), "Incompatible mod created output archives");
        var broken = GArchive.Create("broken");
        broken["wrong/prefix"] = [1];
        manager.LoadArchive(broken);
        var failed = Path.Combine(root, "failed");
        Reject(() => compiler.SaveTo(failed, new Dictionary<string, byte[]> { ["fixture/text"] = [1], ["broken/new"] = [2] }));
        Check(!Directory.Exists(failed) && !Directory.EnumerateDirectories(root, ".eastward-compile-*").Any(), "A failed archive save left partial output");
        using (var zip = ZipFile.Open(pathToMod, ZipArchiveMode.Update))
        {
            zip.GetEntry("resources/fixture/text")!.Delete();
            using var entry = zip.CreateEntry("resources/fixture/text").Open();
            entry.Write("wrong!"u8);
        }
        Reject(() => ModPackage.Read(pathToMod));
    }
    finally { Directory.Delete(root, true); }
}

static void AuditCompilation(string sourceRoot, string[] resourceRoots)
{
    var manager = AssetManager.Create(Path.Combine(resourceRoots[0], "config.g"));
    manager.RootDirectory = resourceRoots[0];
    foreach (var directory in resourceRoots)
        foreach (var file in Directory.EnumerateFiles(directory, "*.g", SearchOption.AllDirectories)) manager.LoadArchive(GArchive.Read(file));
    var metadata = SourceProjectMetadata.Read(sourceRoot);
    var mappings = metadata.PathRemappings.ToDictionary(pair => pair.Value, pair => pair.Key);
    var compiler = new AssetCompiler(manager);
    var failures = new List<string>();
    var counts = new Dictionary<string, int>();
    var selectedTypes = Environment.GetEnvironmentVariable("EASTWARD_COMPILE_TYPES")?.Split(',').ToHashSet(StringComparer.Ordinal);
    foreach (var (name, path) in metadata.Assets)
    {
        if (!manager.Assets.TryGetValue(name, out var info) || info.Type == "folder" || info.FilePath == null) continue;
        if (selectedTypes != null && !selectedTypes.Contains(info.Type)) continue;
        try
        {
            var resources = compiler.Compile(new Dictionary<string, string> { [name] = SafePath.ResolveWithin(sourceRoot, path) }, mappings);
            Check(resources.Count > 0, "No compiled resources");
            counts[info.Type] = counts.GetValueOrDefault(info.Type) + 1;
        }
        catch (Exception error) { failures.Add(info.Type + " " + name + ": " + error.Message); }
        manager.UnloadAssets();
    }
    foreach (var (type, count) in counts.OrderBy(pair => pair.Key)) Console.WriteLine($"COMPILE {type}: {count}");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Check(failures.Count == 0, "Source compilation failures: " + failures.Count);
}

static void Reject(Action action)
{
    try
    {
        action();
    }
    catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException) { return; }
    throw new Exception("Invalid input was accepted");
}

static void Archive()
{
    var archive = GArchive.Create("테스트");
    archive["테스트/경로/🐈.txt"] = Encoding.UTF8.GetBytes("안녕");
    archive["테스트/empty"] = [];
    archive["테스트/zero"] = [];
    var overlay = GArchive.Create("테스트");
    overlay["테스트/extra"] = [1, 2];
    overlay["테스트/empty"] = [3];
    archive.AddOverlay(overlay);
    using var stream = new MemoryStream();
    stream.Write(new byte[12]);
    archive.Write(stream, true);
    stream.Position = 12;
    var restored = GArchive.Read("테스트", stream, true);
    Check(restored.Count == 4 && restored.All(pair => pair.Value.SequenceEqual(archive[pair.Key])), "Archive or overlay differs");
    stream.Position = 0;
    GArchive.Create("empty").Write(stream, true);
    Check(stream.Length == 8 && stream.CanRead, "Overwrite did not truncate or closed caller stream");
    var invalid = stream.ToArray();
    BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(4), -1);
    Reject(() => GArchive.Read("invalid", invalid));
    // An uncompressed entry retains its compression mode when rewritten.
    using var raw = new MemoryStream();
    using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
    {
        writer.Write(27191);
        writer.Write(1);
        writer.WriteZString("a");
        writer.Write(26);
        writer.Write(0);
        writer.Write(3);
        writer.Write(3);
        writer.Write(new byte[] { 1, 2, 3 });
    }
    var original = GArchive.Read("raw", raw.ToArray());
    using var rewritten = new MemoryStream();
    original.Write(rewritten, true);
    Check(BinaryPrimitives.ReadInt32LittleEndian(rewritten.ToArray().AsSpan(14)) == 0, "Compression mode changed");
}

static void Image()
{
    byte[] pixels = [13, 25, 42, 0, 255, 17, 28, 128, 1, 2, 3, 255, 4, 5, 6, 100];
    var hmg = new HmgAsset(new RgbaImage(2, 2, pixels));
    var native = hmg.Encode();
    Check(hmg.Header!.PaddingA.Length is >= 1 and <= 4 && hmg.Header.PaddingB.Length is >= 1 and <= 4, "New HMG does not use native padding ranges");
    Check(HmgHeader.Read(native).PayloadOffset + hmg.Header.CompressedSize == native.Length, "HMG header offsets differ");
    var encoded = hmg.Encode([], []);
    var padded = new byte[encoded.Length + 5];
    encoded.AsSpan(0, 8).CopyTo(padded);
    padded[3] = 2;
    padded.AsSpan(8, 2).Fill(0xa5);
    padded.AsSpan(26, 3).Fill(0x5a);
    encoded.AsSpan(8, 16).CopyTo(padded.AsSpan(10));
    padded[24] = 3;
    padded[22] = 24;
    padded[23] = 7;
    encoded.AsSpan(24).CopyTo(padded.AsSpan(29));
    BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(4), padded.Length);
    var asset = Asset.Create<HmgAsset>(padded);
    var decoded = asset.Image!;
    Check(asset.Header!.PixelWidthDeclaration == 24 && asset.Header.Acceleration == 7, "HMG metadata was discarded or interpreted as a format selector");
    var paddingA = asset.Header.PaddingA.ToArray();
    var paddingB = asset.Header.PaddingB.ToArray();
    var rewritten = asset.Encode();
    Check(asset.Header.PaddingA.Span.SequenceEqual(paddingA) && asset.Header.PaddingB.Span.SequenceEqual(paddingB), "Re-encoding changed padding bytes");
    Check(asset.Header.PaddingA.Length == 2 && asset.Header.PaddingB.Length == 3 && Asset.Create<HmgAsset>(rewritten).Image!.Pixels.SequenceEqual(pixels), "Re-encoding lost HMG padding or pixels");
    var unsupported = (byte[])padded.Clone();
    unsupported[25] = 1;
    Check(HmgHeader.Read(unsupported).AlternateCopyFlag == 1, "Cannot inspect alternate-copy metadata");
    Reject(() => Asset.Create<HmgAsset>(unsupported));
    Reject(() => asset.Decode(new MemoryStream(unsupported)));
    Check(ReferenceEquals(asset.Image, decoded) && asset.Header.PaddingA.Span.SequenceEqual(paddingA), "Failed decode replaced loaded image or header");
    var huge = (byte[])encoded.Clone();
    BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(12), int.MaxValue);
    BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(16), int.MaxValue);
    Reject(() => Asset.Create<HmgAsset>(huge));
    Check(decoded.Pixels.SequenceEqual(pixels), "Padded HMG differs");
    using var png = new MemoryStream(decoded.ToPng());
    Check(RgbaImage.Load(png).Pixels.SequenceEqual(pixels), "PNG lost transparent RGB");
    Check(decoded.Crop(1, 0, 1, 2).Pixels.SequenceEqual(new byte[] { 255, 17, 28, 128, 4, 5, 6, 100 }), "Crop differs");
    Check(decoded.ToStraightAlpha().Pixels[0] == 0 && pixels[0] == 13, "Alpha conversion mutated source");
    Reject(() => decoded.Crop(-1, 0, 1, 1));
    padded[^1] ^= 1;
    var truncated = encoded[..^1];
    Reject(() => Asset.Create<HmgAsset>(truncated));
}

static void Lua()
{
    var data = LuaTable.Parse("-- comment\nreturn { ['guid'] = '\\237\\149\\156', text = [=[\n안녕]=], enabled=true, nested={1,2}, }")!;
    Check(data["guid"]!.ToString() == "한" && data["text"]!.ToString() == "안녕" && data["nested"]!["2"]!.GetValue<double>() == 2, "Lua table differs");
    Reject(() => LuaTable.Parse("return os.execute('bad')"));
    Reject(() => LuaTable.Parse("return { a=1 b=2 }"));
}

static void Metadata()
{
    const string assets = """
        {"folder":{"type":"folder","filePath":false},
         "folder/item":{"type":"text","filePath":"game/asset/folder/item","fileType":"file","objectFiles":{"data":"fixture/old","cache":false},"tags":["hello"],"dependency":{"texture":"images/a"},"properties":{},"deployMeta":{},"fileTime":1690344121.0853960514,"managerVersion":"3","unknown":{"a":[1,false,null]}},
         "empty":{"type":"raw","objectFiles":[],"properties":[]}}
        """;
    const string scripts = """
        {"source":{"module":"folder/code","alias":"folder/code"},"export":{"module":"fixture/code","alias":"fixture/code"},"unknown":{"keep":true}}
        """;
    const string textures = """
        {"root":"1","unknown":false,"map":{
          "1":{"model":"Library","body":{"groups":["2","3"],"allowPacked":true}},
          "2":{"model":"Group","body":{"name":"atlas","textures":["4"],"atlasMode":true,"atlasCachePath":"fixture/atlas","filter":"linear","default":true}},
          "3":{"model":"Group","body":{"name":"empty","textures":{},"atlasMode":false}},
          "4":{"model":"Texture","unknown":[false],"body":{"path":"images/a","parent":"2","atlasId":1,"x":0,"y":0,"w":1,"h":1,"ow":2,"oh":2,"rotated":false}},
          "unused":{"model":"Future","body":{"keep":[1,2]}}
        }}
        """;
    var index = AssetIndex.Create(assets);
    var library = ScriptLibrary.Create(scripts);
    var textureIndex = TextureIndex.Create(textures);
    Check(JsonNode.DeepEquals(JsonNode.Parse(assets), JsonNode.Parse(index.ToJson())), "Asset metadata round trip lost fields or types");
    Check(JsonNode.DeepEquals(JsonNode.Parse(scripts), JsonNode.Parse(library.ToJson())), "Script module identity or aliases were lost");
    Check(JsonNode.DeepEquals(JsonNode.Parse(textures), JsonNode.Parse(textureIndex.ToJson())), "Texture serializer envelope, empty group or unknown object was lost");
    Check(index["empty"].ObjectFiles.Count == 0 && JsonNode.DeepEquals(JsonNode.Parse(assets), index.Data), "Reading an empty path table changed the source document");
    Check(index["folder/item"].Tags!.Count == 1 && index["folder/item"].Dependency!.ContainsKey("texture"), "Asset tags or dependencies were not exposed");
    Check(library.Count == 1 && textureIndex.Groups.Count == 2, "Alias lookup or empty texture group differs");

    var manager = new AssetManager(index, library, textureIndex);
    var archive = GArchive.Create("fixture");
    archive["fixture/old"] = "old"u8.ToArray();
    archive["fixture/new"] = "new"u8.ToArray();
    manager.LoadArchive(archive);
    var old = manager.LoadAsset("folder/item");
    index["folder/item"].ObjectFiles["data"] = "fixture/new";
    Check(((TextAsset)manager.LoadAsset("folder/item")!).Text == "new" && !ReferenceEquals(old, manager.LoadAsset("folder/item")), "Metadata editing retained cached resources");
    index["folder/item"].Parent = "elsewhere";
    Check(manager.GetChildren("folder").Count == 0 && manager.GetChildren("elsewhere").Count == 1, "Parent editing retained stale child lookup");
    Reject(() => index["folder/item"].Parent = "folder/item");
    Check(index["folder/item"].Parent == "elsewhere", "Invalid parent edit was not rolled back");

    var beforeAssets = index.ToJson(false);
    var beforeScripts = library.ToJson(false);
    var beforeTextures = textureIndex.ToJson(false);
    var beforeResource = manager.LoadAsset("folder/item");
    Reject(() => manager.EditMetadata((entries, code, images) =>
    {
        entries["folder/item"].Tags!.Add("bad edit");
        code.SetModule("new", "folder/other", "fixture/other");
        images.Data["map"]!["1"]!["body"]!["groups"] = new JsonArray("missing");
    }));
    Check(index.ToJson(false) == beforeAssets && library.ToJson(false) == beforeScripts && textureIndex.ToJson(false) == beforeTextures, "Failed batch partially changed metadata");
    Check(ReferenceEquals(beforeResource, manager.LoadAsset("folder/item")), "Failed batch discarded an unchanged resource cache");

    manager.EditMetadata((entries, code, images) =>
    {
        entries["folder/item"].Tags!.Add("modded");
        entries["folder/item"].ManagerVersion = JsonValue.Create(4);
        entries["folder/item"].Data["future"] = new JsonArray(false, 7);
        entries["folder/item"].Parent = "folder";
        code["folder/code"] = "fixture/new-code";
        code.SetModule("new", "folder/new-code", "fixture/new-code");
        images.Update("4", body => body["path"] = "images/renamed");
        images.Update("2", body => body["atlasCachePath"] = "fixture/new-atlas");
    });
    Check(manager.GetChildren("folder").Count == 1 && textureIndex["images/renamed"] == "fixture/new-atlas", "Batch edit did not rebuild lookups");
    Check(library.Data["export"]!["alias"]!.ToString() == "fixture/new-code", "Changing a script path broke its module alias");
    Check(index["folder/item"].ObjectFiles.Remove("data") && index.Data["folder/item"]!["objectFiles"]!["cache"]!.ToJsonString() == "false", "Removing a path discarded an unknown role");
    index["folder/item"].ObjectFiles["data"] = "fixture/new";

    index.Rename("folder", "renamed");
    Check(index.ContainsKey("renamed/item") && manager.GetChildren("renamed").Count == 1 && !index.ContainsKey("folder/item"), "Subtree rename did not update asset keys and parents");
    Check(((TextAsset)manager.LoadAsset("renamed/item")!).Text == "new", "Metadata rename moved runtime paths unexpectedly");
    var renameSnapshot = index.ToJson(false);
    Reject(() => index.Rename("renamed", "empty"));
    Check(index.ToJson(false) == renameSnapshot, "Failed rename partially changed entries");
    var added = new AssetInfo("renamed/new", "game/asset/renamed/new", "file", null, new()
    {
        ["data"] = "fixture/new"
    }, "text");
    index.Add("renamed/new", added);
    Check(manager.GetChildren("renamed").Count == 2 && index.Remove("renamed/new") && manager.GetChildren("renamed").Count == 1, "Adding or removing an asset did not update relationships");
    var addSnapshot = index.ToJson(false);
    added.Parent = "cycle";
    Reject(() => index.Add("cycle", added));
    Check(index.ToJson(false) == addSnapshot && !index.Data.ContainsKey("cycle"), "Failed asset addition partially changed the index");
    textureIndex.RemoveTexture("images/renamed");
    Check(textureIndex.Textures.Count == 0 && textureIndex.Groups.Count == 2 && textureIndex.Data["map"]!["4"] != null, "Removing a texture discarded shared serializer data");
    var scriptSnapshot = library.ToJson(false);
    Reject(() => library.SetModule("conflict", "folder/code", "fixture/conflict"));
    Check(library.ToJson(false) == scriptSnapshot, "Failed script edit changed the source document");
    Check(library.RemoveModule("new"), "Script module removal failed");
    textureIndex.AddGroup("5", JsonNode.Parse("{\"model\":\"Group\",\"unknown\":false,\"body\":{\"name\":\"added\",\"textures\":[],\"atlasMode\":false}}")!.AsObject());
    textureIndex.AddTexture("5", "6", JsonNode.Parse("{\"model\":\"Texture\",\"body\":{\"path\":\"images/added\",\"processor\":\"custom\"}}")!.AsObject());
    Check(textureIndex.Textures["images/added"].Group["name"]!.ToString() == "added", "Adding a texture lost its group");
    textureIndex.MoveTexture("images/added", "2");
    Check(textureIndex["images/added"] == "fixture/new-atlas" && textureIndex.Textures["images/added"].Data["parent"]!.ToString() == "2", "Moving a texture did not update group references");
    textureIndex.RemoveGroup("5");
    Check(textureIndex.Groups.Count == 2 && textureIndex.Data["map"]!["5"]!["unknown"]!.ToJsonString() == "false", "Removing a group discarded its serialized entry");
    var textureSnapshot = textureIndex.ToJson(false);
    Reject(() => textureIndex.AddTexture("2", "7", JsonNode.Parse("{\"model\":\"Texture\",\"body\":{\"path\":\"images/added\"}}")!.AsObject()));
    Check(textureIndex.ToJson(false) == textureSnapshot, "Failed texture addition changed the map");

    var tree = new FileTree();
    tree.AppendFileName("pack/child");
    tree.AppendFileName("pack");
    tree.AppendFileName("pack/child");
    tree.RemoveFileName("pack");
    Check(tree.Files.Count == 1 && tree.ToString().Contains("child"), "Removing a parent file removed its virtual children");
    tree.RemoveFileName("pack/child");
    Check(tree.Files.Count == 0 && !tree.ToString().Contains("pack"), "Removing a file left an empty tree branch");
    var dirs = new List<string> { "a", "b" };
    var node = new FileTree.Node();
    node.AddChild(dirs);
    node.RemoveChild(dirs);
    Check(dirs.SequenceEqual(new[] { "a", "b" }), "Tree editing changed the caller's path list");

    var output = Path.Combine(Path.GetTempPath(), "EastwardLib-metadata-" + Guid.NewGuid());
    try
    {
        var assetPath = Path.Combine(output, "config", "asset_index");
        var scriptPath = Path.Combine(output, "config", "script_library");
        var texturePath = Path.Combine(output, "config", "texture_index");
        manager.SaveMetadata(assetPath, scriptPath, texturePath);
        var savedAssets = File.ReadAllText(assetPath);
        Reject(() => manager.SaveMetadata(assetPath, assetPath, texturePath));
        Check(File.ReadAllText(assetPath) == savedAssets, "Conflicting metadata paths overwrote a saved index");
        var loaded = AssetManager.Create(assetPath, scriptPath, texturePath);
        Check(JsonNode.DeepEquals(index.Data, loaded.Index.Data) && JsonNode.DeepEquals(library.Data, loaded.Scripts.Data) && JsonNode.DeepEquals(textureIndex.Data, loaded.Textures.Data), "Edited metadata did not survive file saving");
        var config = GArchive.Create("config");
        config["config/untouched"] = [1, 2, 3];
        config["config/asset_index"] = Encoding.UTF8.GetBytes(assets);
        config["config/asset_index.packed"] = MessagePackSerializer.ConvertFromJson(assets);
        manager.SaveMetadata(config);
        var configPath = Path.Combine(output, "config.g");
        config.Write(configPath);
        var recovered = AssetManager.Create(configPath);
        Check(JsonNode.DeepEquals(index.Data, recovered.Index.Data) && JsonNode.DeepEquals(library.Data, recovered.Scripts.Data) && JsonNode.DeepEquals(textureIndex.Data, recovered.Textures.Data), "Edited metadata did not survive archive saving");
        Check(GArchive.Read(configPath)["config/untouched"].SequenceEqual(new byte[] { 1, 2, 3 }), "Metadata saving changed another archive entry");
        var packedData = AssetIndex.FromPacked(index.Encode(true)).Data;
        Check(JsonNode.DeepEquals(packedData, AssetIndex.FromPacked(GArchive.Read(configPath)["config/asset_index.packed"]).Data), "Metadata saving left a stale packed runtime index");
        var conflicting = AssetIndex.Create(index.ToJson());
        conflicting.Values.First().Data["conflict"] = true;
        config["config/asset_index.packed"] = conflicting.Encode(true);
        config.Write(configPath);
        Reject(() => AssetManager.Create(configPath));
        manager.SaveMetadata(config);
        config.Remove("config/asset_index");
        manager.SaveMetadata(config);
        config.Write(configPath);
        Check(!config.ContainsKey("config/asset_index") && JsonNode.DeepEquals(packedData, AssetManager.Create(configPath).Index.Data), "Packed-only metadata was not preserved");
        var packedPath = assetPath + ".packed";
        manager.SaveMetadata(packedPath, scriptPath, texturePath);
        Check(JsonNode.DeepEquals(packedData, AssetManager.Create(packedPath, scriptPath, texturePath).Index.Data), "Packed metadata file did not survive saving");
    }
    finally
    {
        if (Directory.Exists(output))
        {
            Directory.Delete(output, true);
        }
    }
    Reject(() => AssetIndex.Create("[]"));
    Reject(() => AssetIndex.Create("{"));
    Reject(() => AssetIndex.Create("{\"bad\":null}"));
    Reject(() => AssetIndex.Create("{\"bad\":{\"tags\":[1]}}"));
    Reject(() => AssetIndex.Create("{\"a\":{\"parent\":\"b\"},\"b\":{\"parent\":\"a\"}}"));
    Reject(() => ScriptLibrary.Create("{\"source\":{\"a\":\"x\"}}"));
    Reject(() => TextureIndex.Create("{\"root\":\"missing\",\"map\":{}}"));
    Reject(() => TextureIndex.Create("{\"root\":\"1\",\"map\":{\"1\":{\"body\":{\"groups\":[\"bad\"]}}}}"));
}

static AssetManager Manager(string index) => new(AssetIndex.Create(index), ScriptLibrary.Create("{\"source\":{}}"), TextureIndex.Create("{\"root\":\"1\",\"map\":{\"1\":{\"body\":{\"groups\":[]}}}}"));

static void Scene()
{
    var manager = Manager("{\"world.scene\":{\"type\":\"scene\",\"objectFiles\":{\"def\":\"fixture/src\",\"compiled\":\"fixture/cache\"}}}");
    var archive = GArchive.Create("fixture");
    archive["fixture/src/scene_index.json"] = "{}"u8.ToArray();
    archive["fixture/src/main.scene_group"] = "{\"source\":true}"u8.ToArray();
    archive["fixture/cache/orphan.scene_group.packed"] = MessagePackSerializer.ConvertFromJson("{}");
    manager.LoadArchive(archive);
    var scene = (SceneAsset)manager.LoadAsset("world.scene")!;
    Check(scene.Groups.Count == 1 && scene.Groups["main.scene_group"]!["source"]!.GetValue<bool>(), "Stale groups restored");
}

static void Workbook()
{
    var workbook = new WorkbookAsset(JsonNode.Parse("{\"table\":[{\"name\":\"Sam\\u001d_x0041_\",\"hp\":10}],\"vertical\":[{\"x\":1},{\"x\":2}]}")!, JsonNode.Parse("{\"table\":{\"type\":\"list\",\"keys\":[\"name\",\"hp\"],\"keyMeta\":[\"localizable\",\"\"]},\"vertical\":{\"type\":\"vlist\",\"keys\":[\"x\"],\"keyMeta\":[\"\"]}}")!);
    Check(workbook.GetRows("table")[0][0]!.ToString() == "%name", "Localization header lost");
    Check(workbook.GetRows("vertical").Count == 1 && workbook.GetRows("vertical")[0].Count == 3, "Vertical sheet differs");
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
    try
    {
        workbook.SaveTo(path);
        using var zip = ZipFile.OpenRead(path);
        Check(zip.GetEntry("xl/worksheets/sheet2.xml") != null && zip.GetEntry("[Content_Types].xml") != null, "Incomplete OOXML workbook");
        using var xml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Check(xml.ReadToEnd().Contains("Sam_x001D__x005F_x0041_"), "OOXML control characters or literal escape text lost");
    }
    finally { File.Delete(path); }
}

static void Mesh()
{
    var result = MeshConverter.Convert("<ASSIMP><Scene><Node><Matrix4>-1 0 0 5 0 2 0 0 0 0 1 0 0 0 0 1</Matrix4><MeshRefs>0</MeshRefs></Node><MeshList><Mesh><Positions>0 0 0\n1 0 0\n0 1 0</Positions><Normals>0 0 1\n0 0 1\n0 0 1</Normals><FaceList><Face>0 1 2</Face></FaceList></Mesh></MeshList></Scene></ASSIMP>");
    Check(result.Obj.Contains("v 4 0 0") && result.Obj.Contains("v 5 2 0") && result.Obj.Contains("f 3//3 2//2 1//1"), "Mesh transform or winding differs");
}

static void VirtualAssets()
{
    var manager = Manager("{\"hero.ase\":{\"type\":\"msprite\",\"objectFiles\":{\"def\":\"fixture/sprite\"}},\"hero.ase/idle\":{\"type\":\"deck2d.msprite_seq\",\"objectFiles\":{}},\"hero.ase/atlas\":{\"type\":\"texture\",\"objectFiles\":{\"pixmap\":\"fixture/atlas\"}}}");
    var archive = GArchive.Create("fixture");
    archive["fixture/sprite"] = "{\"atlases\":{\"a\":\"atlas.png\"},\"anims\":{\"0\":{\"name\":\"idle\",\"seq\":[[\"f\",0.1]]}},\"frames\":{\"f\":{\"parts\":[]}},\"modules\":{},\"features\":[]}"u8.ToArray();
    archive["fixture/atlas"] = new HmgAsset(new RgbaImage(1, 1, [0, 0, 0, 0])).Encode();
    manager.LoadArchive(archive);
    var sequence = (JsonAsset)manager.LoadAsset("hero.ase/idle")!;
    Check(sequence.Data!["frames"]!["f"] != null && manager.GetChildren("hero.ase").Count == 2, "Virtual sequence differs");
    var tree = manager.GetIndex();
    tree.AppendFileName("hero.ase");
    Check(tree.Files.Count == 3 && tree.ToString().Contains("idle"), "File tree lost virtual children");
    var output = Path.Combine(Path.GetTempPath(), "EastwardLib-test-" + Guid.NewGuid());
    try
    {
        manager.ExtractTo(output);
        Check(File.Exists(Path.Combine(output, "hero.ase")) && Directory.GetFiles(output).Length == 1, "Lazy export lost parent or exported embedded children");
    }
    finally
    {
        File.Delete(Path.Combine(output, "hero.ase"));
        if (Directory.Exists(output))
        {
            Directory.Delete(output);
        }
    }
    manager.RegisterLoader("msprite", _ => new TextAsset("override"));
    Check(manager.LoadAsset("hero.ase") is TextAsset, "Loader cache not invalidated");
}

static void Paths()
{
    var root = Path.Combine(Path.GetTempPath(), "EastwardLib-tests");
    Reject(() => SafePath.ResolveWithin(root, "../escape"));
    Reject(() => SafePath.ResolveWithin(root, "a\\..\\escape"));
    Reject(() => SafePath.ResolveWithin(root, "C:/escape"));
    Check(SafePath.ResolveWithin(root, "nested/file").StartsWith(root), "Contained path rejected");
}

static void Output()
{
    var output = Path.Combine(Path.GetTempPath(), "EastwardLib-output-" + Guid.NewGuid());
    try
    {
        var asset = new TextAsset("출력");
        Reject(() => asset.SaveTo(null!));
        Reject(() => asset.SaveTo(" "));
        Reject(() => asset.SaveTo("."));
        Reject(() => asset.SaveTo(".."));
        Reject(() => asset.SaveTo(output + Path.DirectorySeparatorChar));
        Reject(() => asset.SaveTo(Path.GetPathRoot(output)!));
        Check(!Directory.Exists(output), "Invalid file path created an output directory");

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(output, "nested", "file.txt"));
        Check(SafePath.PrepareFile(relative) == Path.GetFullPath(relative), "Output utility did not return the absolute path");
        Check(SafePath.PrepareFile("EastwardLib-output.txt") == Path.Combine(Environment.CurrentDirectory, "EastwardLib-output.txt"), "Filename-only output was rejected");
        asset.SaveTo(relative);
        Check(File.ReadAllText(relative) == "출력", "Relative output path or Unicode contents differ");
        Parallel.For(0, 16, index => new BinaryAsset([(byte)index]).SaveTo(Path.Combine(output, "shared", index + ".bin")));
        Check(Directory.GetFiles(Path.Combine(output, "shared")).Length == 16, "Concurrent output preparation lost files");
        Directory.Delete(Path.Combine(output, "nested"), true);
        asset.SaveTo(relative);
        Check(File.ReadAllText(relative) == "출력", "Deleted output directory was not recreated");

        var image = new RgbaImage(1, 1, [13, 25, 42, 0]);
        Reject(() => image.SavePng(output + Path.DirectorySeparatorChar));
        var pngPath = Path.Combine(output, "images", "pixel.png");
        image.SavePng(pngPath);
        Check(RgbaImage.Load(pngPath).Pixels.SequenceEqual(image.Pixels), "PNG output lost pixels");

        var archive = GArchive.Create("fixture");
        archive["fixture/nested/file"] = [1, 2, 3];
        Reject(() => archive.Write(output + Path.DirectorySeparatorChar));
        var archivePath = Path.Combine(output, "archives", "fixture.g");
        archive.Write(archivePath);
        Check(GArchive.Read(archivePath)["fixture/nested/file"].SequenceEqual(new byte[] { 1, 2, 3 }), "Archive file output differs");
        archive.ExtractTo(Path.Combine(output, "extracted"));
        Check(File.ReadAllBytes(Path.Combine(output, "extracted", "fixture", "nested", "file")).SequenceEqual(new byte[] { 1, 2, 3 }), "Archive extraction differs");

        var manager = Manager("{\"locale\":{\"type\":\"locale_pack\",\"objectFiles\":{\"config\":\"fixture/config\",\"data\":\"fixture/locales\"}}}");
        archive["fixture/config"] = "{\"source_locale\":\"en\"}"u8.ToArray();
        archive["fixture/locales/en"] = Encoding.UTF8.GetBytes("return { main = { hello = '안녕' } }");
        manager.LoadArchive(archive);
        manager.LoadAsset("locale")!.SaveTo(Path.Combine(output, "locale"));
        var po = File.ReadAllBytes(Path.Combine(output, "locale", "locales", "en", "main.po"));
        var pot = File.ReadAllBytes(Path.Combine(output, "locale", "templates", "main.pot"));
        Check(po.SequenceEqual(pot) && Encoding.UTF8.GetString(po).Contains("msgstr \"안녕\""), "Locale source template or Unicode text differs");
        Check(!po.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "Locale output gained a UTF-8 BOM");
    }
    finally
    {
        if (Directory.Exists(output))
        {
            Directory.Delete(output, true);
        }
    }
}

static void ProjectReferences()
{
    var paths = ProjectPath.Map(["X/A", "x/a", "CON/file?.txt", "space./%"]);
    Check(paths["X/A"] != paths["x/a"] && paths["X/A"].Contains('~'), "Case collision was not escaped");
    Check(paths["CON/file?.txt"] == "%43ON/file%3F.txt" && paths["space./%"] == "space%2E/%25", "Source path escaping differs");
    Reject(() => ProjectPath.Escape("a/../b"));
    var refs = new SourceReference(new Dictionary<string, string> { ["deck/a.decks.psd"] = "deck/a.deckpack", ["deck/a.deckpack"] = "other" });
    Check(refs.Rewrite("game/asset/deck/a.decks.psd/item deck/a.decks.psd 'deck/a.deckpack' xdeck/a.decks.psd") == "game/asset/deck/a.deckpack/item deck/a.deckpack 'other' xdeck/a.decks.psd", "Reference matching cascaded or replaced a partial name");
    var output = Path.Combine(Path.GetTempPath(), "EastwardLib-proto-" + Guid.NewGuid() + ".scene_group");
    var source = "{\"texture\":\"deck/a.decks.psd\"}";
    try
    {
        File.WriteAllText(output, new JsonObject { ["protos"] = new JsonArray(new JsonObject { ["serialized"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) }, new JsonObject { ["serialized"] = "opaque-data" }) }.ToJsonString());
        Check(refs.RewriteFile(output), "Embedded prototype reference was not rewritten");
        var data = JsonNode.Parse(File.ReadAllText(output))!;
        Check(Encoding.UTF8.GetString(Convert.FromBase64String(data["protos"]![0]!["serialized"]!.ToString())) == refs.Rewrite(source) && data["protos"]![1]!["serialized"]!.ToString() == "opaque-data", "Prototype bytes were corrupted");
    }
    finally { File.Delete(output); }
}

static void SourcePackages()
{
    var longText = new string('한', 10000) + "🐈";
    var bytes = BiffWorkbook.Write([new BiffWorkbook.Sheet("values@raw", new IReadOnlyList<JsonNode?>[] { new JsonNode?[] { JsonValue.Create(longText), JsonValue.Create(3.5), JsonValue.Create(true), null } })]);
    Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }), "XLS has no CFB header");
    var definition = JsonNode.Parse("{\"anims\":{\"a\":{\"name\":\"idle\",\"seq\":[[\"f\",0.1]]}},\"frames\":{\"f\":{\"parts\":[]}},\"modules\":{},\"features\":[]}")!;
    var ase = AseWriter.Write(definition, new Dictionary<string, RgbaImage>(), []);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(ase.AsSpan(6)) == 1, "Source animation duplicated frames");
    var merged = PsdWriter.Write(new RgbaImage(1, 1, [1, 2, 3, 4]));
    Check(merged.AsSpan(40).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Flattened PSD channel pixels differ");
    if (Environment.GetEnvironmentVariable("EASTWARD_TEST_XLS") is string fixture)
    {
        File.WriteAllBytes(fixture, bytes);
    }
}

static void SourceProject()
{
    var manager = Manager("{\"a.decks.psd\":{\"type\":\"deck_pack\",\"filePath\":\"game/asset/a.decks.psd\",\"objectFiles\":{\"export\":\"fixture/decks\"}},\"a.decks.psd/idle:right\":{\"type\":\"deck2d.mquad\",\"objectFiles\":{}},\"scene.proto\":{\"type\":\"proto\",\"filePath\":\"game/asset/scene.proto\",\"objectFiles\":{\"def\":\"fixture/proto\"}}}");
    var archive = GArchive.Create("fixture");
    archive["fixture/decks/decks.json"] = "{\"decks\":[{\"name\":\"idle:right\"}]}"u8.ToArray();
    archive["fixture/decks/decks.hmg"] = new HmgAsset(new RgbaImage(1, 1, [1, 2, 3, 255])).Encode();
    archive["fixture/proto"] = "{\"deck\":\"a.decks.psd/idle:right\"}"u8.ToArray();
    manager.LoadArchive(archive);
    var output = Path.Combine(Path.GetTempPath(), "EastwardLib-project-" + Guid.NewGuid());
    try
    {
        var report = new ProjectReconstructor(manager).SaveTo(output);
        Check(report.Restored.Count == 2 && report.VirtualAssets == 1 && report.Unavailable.Count == 0 && report.PathRemappings.Count == 1, "Project source coverage differs");
        var metadata = SourceProjectMetadata.Read(output);
        Check(metadata.Assets.Count == 2 && metadata.PathRemappings.Count == 1, "Project metadata mappings differ");
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(output, SourceProjectMetadata.FileName)))!.AsObject();
        Check(data.Count == 4 && !File.Exists(output + ".recovery.json"), "Project metadata contains a recovery report");
        var compiled = new AssetCompiler(manager).CompileProject(output, info => info.Type == "proto");
        Check(JsonNode.Parse(compiled["fixture/proto"])!["deck"]!.ToString() == "a.decks.psd/idle:right", "Project compilation did not restore original references");
        data["assets"]!["scene.proto"] = "../outside.proto";
        File.WriteAllText(Path.Combine(output, SourceProjectMetadata.FileName), data.ToJsonString());
        Reject(() => SourceProjectMetadata.Read(output));
        metadata.SaveTo(output);
        Check(File.Exists(Path.Combine(output, "game/asset/a.deckpack/decks.png")), "Deckpack pixels missing");
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(output, "game/asset/scene.proto")))!["deck"]!.ToString() == "a.deckpack/idle:right", "Virtual identifier was escaped or not remapped");
        try
        {
            new ProjectReconstructor(manager).SaveTo(output);
            throw new Exception("Existing project overwritten");
        }
        catch (IOException) { }
    }
    finally
    {
        // Only remove the fresh directory created by this fixture.
        if (Directory.Exists(output))
        {
            Directory.Delete(output, true);
        }


    }
}

static void SourceImages()
{
    var image = new RgbaImage(1, 1, [120, 80, 30, 128]);
    var psd = PsdWriter.Write([new PsdWriter.Layer("그룹", Children: [new PsdWriter.Layer("pixel", image)])]);
    Check(psd.AsSpan(0, 4).SequenceEqual("8BPS"u8) && BinaryPrimitives.ReadInt32BigEndian(psd.AsSpan(18)) == 1, "PSD header differs");
    Check(BinaryPrimitives.ReadInt16BigEndian(psd.AsSpan(42)) == 3 && Encoding.ASCII.GetString(psd).Contains("lsct"), "PSD group records missing");
    var definition = JsonNode.Parse("{\"anims\":{\"a\":{\"name\":\"idle\",\"seq\":[[\"f\",0.1]]}},\"frames\":{\"f\":{\"parts\":[[\"m\",-2,3]],\"meta\":{\"anchors\":{\"hand\":[[1,2,\"#ff0000\"]]}}}},\"modules\":{\"m\":{\"rect\":[0,0,1,1],\"atlas\":\"a\",\"feature\":1}},\"features\":[{\"id\":1,\"name\":\"head.eye\"}]}")!;
    var ase = AseWriter.Write(definition, new Dictionary<string, RgbaImage> { ["a"] = image });
    Check(BinaryPrimitives.ReadUInt16LittleEndian(ase.AsSpan(4)) == 0xa5e0 && BinaryPrimitives.ReadInt32LittleEndian(ase) == ase.Length, "ASE header differs");
    Check(BinaryPrimitives.ReadUInt16LittleEndian(ase.AsSpan(136)) == 100, "ASE duration differs");
    var offset = 144;
    var cels = 0;
    var tags = 0;
    var layers = 0;
    while (offset < ase.Length)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(ase.AsSpan(offset));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(ase.AsSpan(offset + 4));
        if (type == 0x2004)
        {
            layers++;
        }

        if (type == 0x2018)
        {
            tags++;
        }

        if (type == 0x2005)
        {
            cels++;
            if (cels == 1)
            {
                using var compressed = new MemoryStream(ase, offset + 26, length - 26);
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var pixels = new MemoryStream();
                zlib.CopyTo(pixels);
                Check(pixels.ToArray().SequenceEqual(image.Pixels), "ASE cel pixels differ");
            }
        }
        Check(length >= 6, "Invalid ASE chunk length");
        offset += length;
    }
    Check(offset == ase.Length && cels == 3 && tags == 1 && layers == 4, "ASE chunks missing");
}

static void LazyLua()
{
    var calls = 0;
    var asset = new LuaAsset([0x1b, 0x4c, 0x4a], _ => { calls++; return "return 1"; });
    Check(asset.IsBytecode && calls == 0 && asset.Encode().Length == 3, "Lua was decompiled while loading");
    Check(asset.GetSource() == "return 1" && asset.GetSource() == "return 1" && calls == 1, "Lazy Lua cache differs");
    using var stream = new MemoryStream("return 2"u8.ToArray());
    asset.Decode(stream);
    Check(stream.CanRead && asset.GetSource() == "return 2" && calls == 1, "Lua decode closed stream or retained stale source");
}

static void Audit(string[] directories)
{
    var directory = directories[0];
    var manager = AssetManager.Create(Path.Combine(directory, "config.g"));
    manager.RootDirectory = directory;
    foreach (var input in directories)
    {
        foreach (var path in Directory.EnumerateFiles(input, "*.g"))
        {
            manager.LoadArchive(GArchive.Read(path));
        }
    }

    var totalLoaded = 0;
    var totalOmitted = 0;
    var totalMissing = 0;
    var totalFailed = 0;
    foreach (var group in manager.Assets.Values.Where(info => info.Type != "folder").GroupBy(info => info.Type).OrderBy(group => group.Key))
    {
        var loaded = 0;
        var omitted = 0;
        var missing = 0;
        var failed = 0;
        var failures = new List<string>();
        foreach (var info in group)
        {
            try
            {
                var asset = manager.LoadAsset(info.AssetName);
                if (asset != null)
                {
                    if (asset is MSpriteAsset sprite)
                    {
                        sprite.ToAse();
                    }

                    if (asset is TexturePackAsset pack)
                    {
                        pack.ToPsd();
                    }

                    if (asset is WorkbookAsset workbook)
                    {
                        foreach (var name in workbook.Data!.AsObject().Select(pair => pair.Key))
                        {
                            workbook.GetRows(name);
                        }
                    }

                    if (asset is WorkbookAsset expectedBook && Environment.GetEnvironmentVariable("EASTWARD_TEST_WORKBOOKS") is string expectedRoot)
                    {
                        var path = SafePath.ResolveWithin(expectedRoot, info.AssetName + ".json");
                        new JsonAsset(new JsonObject { ["data"] = expectedBook.Data!.DeepClone(), ["metadata"] = expectedBook.Metadata.DeepClone() }).SaveTo(path);
                    }
                    if (asset is SQScriptAsset script)
                    {
                        script.GetSource();
                    }

                    if (asset is GraphAsset graph)
                    {
                        graph.GetGraphMl();
                    }

                    loaded++;
                }
                else
                {
                    omitted++;
                }
            }
            catch (Exception e)
            {
                if (e is FileNotFoundException)
                {
                    missing++;
                }
                else
                {
                    failed++;
                }

                if (failures.Count < 10)
                {
                    failures.Add(info.AssetName + ": " + e.GetType().Name + " " + e.Message);
                }
            }
        }
        Console.WriteLine($"AUDIT {group.Key}: {loaded}/{group.Count()} validated, {omitted} omitted, {missing} missing, {failed} failed");
        foreach (var failure in failures)
        {
            Console.WriteLine("  " + failure);
        }

        totalLoaded += loaded;
        totalOmitted += omitted;
        totalMissing += missing;
        totalFailed += failed;
    }
    Console.WriteLine($"SUMMARY {totalLoaded} validated, {totalOmitted} omitted, {totalMissing} missing, {totalFailed} failed; {manager.Assets.Count} index entries, {manager.SupportedTypes.Count} loaders");
    if (totalFailed > 0)
    {
        Environment.ExitCode = 1;
    }
}

static void Native(string library, string? archivePath)
{
    using var decompiler = new NativeLuaDecompiler(library);
    Check(!decompiler.IsLoaded, "Native library loaded in constructor");
    var text = new LuaAsset("return 1"u8.ToArray(), decompiler.Decompile);
    Check(text.GetSource() == "return 1" && !decompiler.IsLoaded, "Plain Lua loaded the native library");
    Reject(() => decompiler.Decompile([0x1b, 0x4c, 0x4a]));
    Check(decompiler.IsLoaded, "Native library did not load on demand");
    if (archivePath != null)
    {
        var archive = GArchive.Read(archivePath);
        var fixture = archive.Where(pair => pair.Value.AsSpan().StartsWith(new byte[] { 0x1b, 0x4c, 0x4a })).OrderBy(pair => pair.Value.Length).First();
        var source = decompiler.Decompile(fixture.Value);
        Check(source.Length > 0, "Native decompiler returned empty source");
        for (var i = 0; i < 10; i++)
        {
            Check(decompiler.Decompile(fixture.Value) == source, "Native output differs between calls");
        }

        Console.WriteLine("PASS native installed-bytecode decompilation: " + fixture.Key);
    }
    Console.WriteLine("PASS native lazy loading, diagnostics and repeated calls");
}

static void Graphs()
{
    var script = "return (function()\nnodelist={['start']={localName='start',type='state'};['done']={localName='done',type='state'};};\nnodelist['start'].next={['$done']='done';[true]='done';}; return nodelist end) ()";
    var graph = System.Xml.Linq.XDocument.Parse(SourceGraph.FromFsm(script));
    System.Xml.Linq.XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
    Check(graph.Descendants(ns + "node").Count() == 2 && graph.Descendants(ns + "edge").Count() == 1, "FSM fallback edge was duplicated");
    Reject(() => FsmSource.Parse("return os.execute('bad')"));
    Reject(() => FsmSource.Parse(script + " os.execute('bad')"));
    var quest = JsonNode.Parse("{\"nodes\":[{\"id\":\"a\",\"name\":\"<&\",\"transition\":{\"flag\":1}},{\"id\":\"b\",\"name\":\"next\"}],\"connections\":[{\"from\":\"a\",\"to\":\"b\",\"type\":\"spawn\",\"cond\":\"ready\"}]}")!;
    var xml = System.Xml.Linq.XDocument.Parse(SourceGraph.Write("quest_scheme", quest));
    Check(xml.Descendants(ns + "data").Any(node => (string?)node.Attribute("key") == "d2") && xml.ToString().Contains("dashed"), "Quest transition or spawn styling lost");
    quest["connections"]![0]!["cond"] = "false";
    Check(SourceGraph.Write("quest_scheme", quest).Contains(">false</"), "Literal false edge label was omitted");
    var story = JsonNode.Parse("""
        {"rootNodes":[{"id":"g","fullId":"g","type":"GROUP","text":"group","group":false,"visual":{},"children":[{"id":"a","fullId":"a","type":false,"text":"未知🐈","group":"g","visual":{}},{"id":"b","fullId":"b","type":"NODE","text":"next","group":"g","visual":{}},{"id":"c","fullId":"c","type":"NODE","text":"end","group":"g","visual":{}}]}],"edges":[{"src":"a","dst":"b","text":"normal","type":"NORMAL","visual":{}},{"src":"b","dst":"a","text":"not","type":"NOT","visual":{}},{"src":"a","dst":"c","text":"comment","type":"COMMENT","visual":{}}]}
        """)!;
    var storyXml = SourceGraph.Write("story_graph", story);
    Check(storyXml.Contains("#FF0000") && storyXml.Contains("dashed") && !storyXml.Contains("type=false"), "Story types or edge styles lost");
    if (Environment.GetEnvironmentVariable("EASTWARD_TEST_STORY") is string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, storyXml);
        new JsonAsset(story).SaveTo(output + ".runtime.json");
    }
}

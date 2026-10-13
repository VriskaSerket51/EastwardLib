using System.Globalization;
using System.Xml.Linq;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class MeshCompiler
{
    public static string Compile(string source, string original)
    {
        var document = XDocument.Parse(original);
        var scene = document.Root?.Element("Scene") ?? throw new InvalidDataException("Missing original Assimp scene");
        var materials = scene.Element("MaterialList")?.Elements("Material").ToArray() ?? [];
        var materialNames = materials.Select(material => material.Element("MatPropertyList")?.Elements("MatProperty").FirstOrDefault(property => (string?)property.Attribute("key") == "?mat.name")?.Value.Trim().Trim('"')).ToArray();
        var positions = new List<string>();
        var normals = new List<string>();
        var uvs = new List<string>();
        var faces = new Dictionary<int, List<(int Position, int Uv, int Normal)[]>>();
        var material = 0;
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            var command = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..].Trim();
            if (command is "v" or "vn" or "vt")
            {
                var numbers = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(text => double.Parse(text, CultureInfo.InvariantCulture)).ToArray();
                var dimensions = command == "vt" ? 2 : 3;
                if (numbers.Length != dimensions || numbers.Any(number => !double.IsFinite(number)))
                {
                    throw new InvalidDataException("Unsupported OBJ coordinate");
                }

                (command == "v" ? positions : command == "vn" ? normals : uvs).Add(string.Join(' ', numbers.Select(number => number.ToString("R", CultureInfo.InvariantCulture))));
            }
            else if (command == "usemtl")
            {
                material = Array.IndexOf(materialNames, value);
                if (material < 0)
                {
                    throw new InvalidDataException("Keep existing mesh material names: " + value);
                }
            }
            else if (command == "f")
            {
                int Index(string text, int count)
                {
                    var index = int.Parse(text, CultureInfo.InvariantCulture);
                    index = index < 0 ? count + index : index - 1;
                    return index >= 0 && index < count ? index : throw new InvalidDataException("Invalid OBJ vertex index");
                }
                var vertices = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(text =>
                {
                    var parts = text.Split('/');
                    if (parts.Length > 3)
                    {
                        throw new InvalidDataException("Invalid OBJ face reference");
                    }

                    return (Position: Index(parts[0], positions.Count), Uv: parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], uvs.Count) : -1, Normal: parts.Length > 2 && parts[2].Length > 0 ? Index(parts[2], normals.Count) : -1);
                }).ToArray();
                if (vertices.Length != 3)
                {
                    throw new NotSupportedException("Triangulate OBJ faces before importing");
                }

                if (!faces.TryGetValue(material, out var list))
                {
                    faces[material] = list = [];
                }

                list.Add(vertices);
            }
            else if (command is not ("o" or "g" or "s" or "mtllib"))
            {
                throw new NotSupportedException("Unsupported OBJ statement: " + command);
            }
        }
        if (faces.Count == 0)
        {
            throw new InvalidDataException("OBJ contains no faces");
        }

        var meshList = new XElement("MeshList", new XAttribute("num", faces.Count));
        foreach (var (materialIndex, meshFaces) in faces)
        {
            var vertices = meshFaces.SelectMany(face => face).Distinct().ToArray();
            var indices = vertices.Select((vertex, index) => (vertex, index)).ToDictionary(pair => pair.vertex, pair => pair.index);
            var hasUvs = vertices.All(vertex => vertex.Uv >= 0);
            var hasNormals = vertices.All(vertex => vertex.Normal >= 0);
            if (!hasUvs && vertices.Any(vertex => vertex.Uv >= 0) || !hasNormals && vertices.Any(vertex => vertex.Normal >= 0))
            {
                throw new InvalidDataException("Mixed OBJ vertex attributes");
            }

            var mesh = new XElement("Mesh", new XAttribute("types", "triangles"), new XAttribute("material_index", materialIndex),
                new XElement("Positions", new XAttribute("num", vertices.Length), new XAttribute("set", 0), new XAttribute("num_components", 3), string.Join('\n', vertices.Select(vertex => positions[vertex.Position]))),
                hasNormals ? new XElement("Normals", new XAttribute("num", vertices.Length), new XAttribute("set", 0), new XAttribute("num_components", 3), string.Join('\n', vertices.Select(vertex => normals[vertex.Normal]))) : null,
                hasUvs ? new XElement("TextureCoords", new XAttribute("num", vertices.Length), new XAttribute("set", 0), new XAttribute("num_components", 2), string.Join('\n', vertices.Select(vertex => uvs[vertex.Uv]))) : null,
                new XElement("FaceList", new XAttribute("num", meshFaces.Count), meshFaces.Select(face => new XElement("Face", new XAttribute("num", 3), string.Join(' ', face.Select(vertex => indices[vertex]))))));
            meshList.Add(mesh);
        }
        scene.Element("MeshList")?.Remove();
        scene.Add(meshList);
        scene.Elements("Node").Remove();
        scene.AddFirst(new XElement("Node", new XAttribute("name", "Root"), new XElement("Matrix4", "1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1"), new XElement("MeshRefs", new XAttribute("num", faces.Count), string.Join(' ', Enumerable.Range(0, faces.Count)))));
        return document.ToString();
    }
}

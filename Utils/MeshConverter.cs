using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace EastwardLib.Utils;

public static class MeshConverter
{
    public sealed record Result(string Obj, string Mtl, IReadOnlyCollection<string> Textures);
    private sealed record Mesh(int Material, double[][] Positions, double[][] Normals, double[][] Uvs, int[][] Faces);
    private static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static Result Convert(string xml)
    {
        var scene = XDocument.Parse(xml).Root?.Element("Scene") ?? throw new InvalidDataException("Missing Assimp scene");
        var meshes = scene.Element("MeshList")?.Elements("Mesh").Select(mesh => new Mesh(
            (int?)mesh.Attribute("material_index") ?? 0,
            Vectors(mesh.Element("Positions"), 3), Vectors(mesh.Element("Normals"), 3), Vectors(mesh.Element("TextureCoords"), 2),
            mesh.Element("FaceList")?.Elements("Face").Select(face => Words(face.Value).Select(int.Parse).ToArray()).ToArray() ?? [])).ToArray() ?? [];
        var transformed = new List<Mesh>();
        foreach (var node in scene.Elements("Node"))
        {
            Visit(node, Identity, meshes, transformed);
        }

        if (transformed.Count == 0)
        {
            transformed.AddRange(meshes);
        }

        var textures = new HashSet<string>(StringComparer.Ordinal);
        var materials = new List<string>();
        var mtl = new StringBuilder();
        foreach (var material in scene.Element("MaterialList")?.Elements("Material") ?? [])
        {
            var properties = material.Element("MatPropertyList")?.Elements("MatProperty").ToArray() ?? [];
            string Property(string key, string fallback) => properties.FirstOrDefault(p => (string?)p.Attribute("key") == key)?.Value.Trim().Trim('"') ?? fallback;
            var name = Property("?mat.name", "Material_" + materials.Count);
            materials.Add(name);
            mtl.AppendLine("newmtl " + name).AppendLine("Ka " + Property("$clr.ambient", "0 0 0"))
                .AppendLine("Kd " + Property("$clr.diffuse", "1 1 1")).AppendLine("Ks " + Property("$clr.specular", "0 0 0"))
                .AppendLine("d " + Property("$mat.opacity", "1"));
            foreach (var property in properties.Where(p => (string?)p.Attribute("key") == "$tex.file"))
            {
                var usage = (string?)property.Attribute("tex_usage");
                if (usage is not ("Diffuse" or "Normals"))
                {
                    continue;
                }

                var file = Path.GetFileNameWithoutExtension(property.Value.Trim().Trim('"').Replace('\\', '/')) + ".png";
                textures.Add(file);
                mtl.AppendLine((usage == "Diffuse" ? "map_Kd " : "map_bump ") + file);
            }
            mtl.AppendLine();
        }
        var obj = new StringBuilder("mtllib output.mtl\n\n");
        var vertices = 0;
        var uvs = 0;
        var normals = 0;
        for (var m = 0; m < transformed.Count; m++)
        {
            var mesh = transformed[m];
            obj.AppendLine("o Mesh_" + m);
            if (mesh.Material >= 0 && mesh.Material < materials.Count)
            {
                obj.AppendLine("usemtl " + materials[mesh.Material]);
            }

            foreach (var p in mesh.Positions)
            {
                obj.AppendLine("v " + Vector(p));
            }

            foreach (var p in mesh.Uvs)
            {
                obj.AppendLine("vt " + Vector(p));
            }

            foreach (var p in mesh.Normals)
            {
                obj.AppendLine("vn " + Vector(p));
            }

            foreach (var face in mesh.Faces)
            {
                var refs = face.Select(index =>
                {
                    if (index < 0 || index >= mesh.Positions.Length || (mesh.Uvs.Length > 0 && index >= mesh.Uvs.Length) || (mesh.Normals.Length > 0 && index >= mesh.Normals.Length))
                    {
                        throw new InvalidDataException("Invalid mesh face index");
                    }

                    var v = (index + vertices + 1).ToString(CultureInfo.InvariantCulture);
                    return mesh.Uvs.Length > 0 || mesh.Normals.Length > 0
                        ? v + "/" + (mesh.Uvs.Length > 0 ? (index + uvs + 1).ToString(CultureInfo.InvariantCulture) : "") + (mesh.Normals.Length > 0 ? "/" + (index + normals + 1) : "") : v;
                });
                obj.AppendLine("f " + string.Join(" ", refs));
            }
            vertices += mesh.Positions.Length;
            uvs += mesh.Uvs.Length;
            normals += mesh.Normals.Length;
            obj.AppendLine();
        }
        return new Result(obj.ToString(), mtl.ToString(), textures);
    }

    private static void Visit(XElement node, double[] parent, Mesh[] meshes, List<Mesh> output)
    {
        var local = node.Element("Matrix4") is XElement matrix ? Words(matrix.Value).Select(Number).ToArray() : Identity;
        if (local.Length != 16)
        {
            throw new InvalidDataException("Invalid mesh node matrix");
        }

        var world = Enumerable.Range(0, 16).Select(i => Enumerable.Range(0, 4).Sum(k => parent[i / 4 * 4 + k] * local[k * 4 + i % 4])).ToArray();
        var a = world[0];
        var b = world[1];
        var c = world[2];
        var d = world[4];
        var e = world[5];
        var f = world[6];
        var g = world[8];
        var h = world[9];
        var i = world[10];
        double[] cofactors = [e * i - f * h, f * g - d * i, d * h - e * g, c * h - b * i, a * i - c * g, b * g - a * h, b * f - c * e, c * d - a * f, a * e - b * d];
        var determinant = a * cofactors[0] + b * cofactors[1] + c * cofactors[2];
        foreach (var index in Words(node.Element("MeshRefs")?.Value ?? "").Select(int.Parse))
        {
            if (index < 0 || index >= meshes.Length)
            {
                throw new InvalidDataException("Invalid mesh node reference");
            }

            var mesh = meshes[index];
            if (determinant == 0 && mesh.Normals.Length > 0)
            {
                throw new InvalidDataException("Singular mesh normal transform");
            }

            var positions = mesh.Positions.Select(p => Enumerable.Range(0, 3).Select(row => world[row * 4] * p[0] + world[row * 4 + 1] * p[1] + world[row * 4 + 2] * p[2] + world[row * 4 + 3]).ToArray()).ToArray();
            var normals = mesh.Normals.Select(p =>
            {
                var n = Enumerable.Range(0, 3).Select(row => Enumerable.Range(0, 3).Sum(col => cofactors[row * 3 + col] * p[col]) / determinant).ToArray();
                var length = Math.Sqrt(n.Sum(v => v * v));
                return n.Select(v => length == 0 ? 0 : v / length).ToArray();
            }).ToArray();
            output.Add(mesh with
            {
                Positions = positions,
                Normals = normals,
                Faces = determinant < 0 ? mesh.Faces.Select(face => face.Reverse().ToArray()).ToArray() : mesh.Faces
            });
        }
        foreach (var child in node.Element("NodeList")?.Elements("Node") ?? [])
        {
            Visit(child, world, meshes, output);
        }
    }

    private static string[] Words(string text)
    {
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static double Number(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new InvalidDataException("Invalid mesh number");
    }

    private static double[][] Vectors(XElement? element, int size)
    {
        return (element?.Value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var values = Words(line).Take(size).Select(Number).ToArray();
            return values.Length == size ? values : throw new InvalidDataException("Invalid mesh vector");
        }).ToArray();
    }

    private static string Vector(double[] values)
    {
        return string.Join(" ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }
}

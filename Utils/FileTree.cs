using System.Text;

namespace EastwardLib.Utils;

public class FileTree
{
    public sealed class Node
    {
        public enum Type
        {
            Directory,
            File
        }

        public Type NodeType;
        public string Name = string.Empty;
        public Dictionary<string, Node>? Children;

        public void AddChild(List<string> fileDirs)
        {
            ArgumentNullException.ThrowIfNull(fileDirs);
            if (fileDirs.Count == 0)
            {
                throw new ArgumentException("A file name is required", nameof(fileDirs));
            }
            var parent = this;
            for (var index = 0; index < fileDirs.Count; index++)
            {
                parent.Children ??= new(StringComparer.Ordinal);
                var name = fileDirs[index];
                if (!parent.Children.TryGetValue(name, out var child))
                {
                    child = new Node { NodeType = Type.Directory, Name = name };
                    parent.Children.Add(name, child);
                }
                if (index == fileDirs.Count - 1)
                {
                    child.NodeType = Type.File;
                }
                parent = child;
            }
        }

        public void RemoveChild(List<string> fileDirs)
        {
            ArgumentNullException.ThrowIfNull(fileDirs);
            if (fileDirs.Count == 0)
            {
                throw new ArgumentException("A file name is required", nameof(fileDirs));
            }
            var branch = new List<(Node Parent, string Name)>();
            var parent = this;
            foreach (var name in fileDirs)
            {
                if (parent.Children == null || !parent.Children.TryGetValue(name, out var child))
                {
                    return;
                }
                branch.Add((parent, name));
                parent = child;
            }
            parent.NodeType = Type.Directory;
            for (var index = branch.Count - 1; index >= 0; index--)
            {
                var (node, name) = branch[index];
                var child = node.Children![name];
                if (child.NodeType == Type.File || child.Children?.Count > 0)
                {
                    break;
                }
                node.Children.Remove(name);
            }
        }
    }

    private Node _root = new()
    {
        NodeType = Node.Type.Directory,
        Name = "root"
    };

    private readonly List<string> _files = new();
    private readonly HashSet<string> _fileNames = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Files => _files;

    public void AppendFileName(string fileName)
    {
        ValidateName(fileName);
        if (!_fileNames.Add(fileName))
        {
            return;
        }
        _root.AddChild(fileName.Split('/').ToList());
        _files.Add(fileName);
    }

    public void RemoveFileName(string fileName)
    {
        ValidateName(fileName);
        if (!_fileNames.Remove(fileName))
        {
            return;
        }
        _root.RemoveChild(fileName.Split('/').ToList());
        _files.Remove(fileName);
    }

    private static void ValidateName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Invalid file tree path", nameof(fileName));
        }
    }

    public override string ToString()
    {
        return ToString(1);
    }

    public string ToString(int indent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indent);
        StringBuilder sb = new StringBuilder();
        ToStringHelper(sb, indent, _root);
        return sb.ToString();
    }

    private static void ToStringHelper(StringBuilder sb, int indent, Node node, int depth = 0, bool isLast = false,
        int lastDepth = 0)
    {
        if (depth != 0)
        {
            for (int i = 0; i < lastDepth; i++)
            {
                sb.Append('│');
                sb.Append(' ', indent);
            }

            sb.Append(' ', (depth - lastDepth - 1) * (indent + 1));

            sb.Append(isLast ? '└' : '├');
        }

        switch (node.NodeType)
        {
            case Node.Type.Directory when node.Children == null || node.Children.Count == 0:
                sb.Append("📁");
                break;
            case Node.Type.Directory:
                sb.Append("📂");
                break;
            case Node.Type.File:
                sb.Append("📄");
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        sb.AppendLine(node.Name);

        if (node.Children == null)
        {
            return;
        }

        int idx = 0;
        foreach (var (_, childNode) in node.Children)
        {
            bool childIsLast = idx == node.Children.Count - 1;
            ToStringHelper(sb, indent, childNode, depth + 1, childIsLast, isLast ? lastDepth : depth);
            idx++;
        }
    }
}

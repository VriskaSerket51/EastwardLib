using System.Text;

namespace EastwardLib.MetaData;

public class FileTree
{
    public struct Node
    {
        public enum Type
        {
            Directory,
            File
        }

        public Type NodeType;
        public string Name;
        public Dictionary<string, Node>? Children;

        public void AddChild(List<string> fileDirs)
        {
            Children ??= new Dictionary<string, Node>();

            if (fileDirs.Count == 1)
            {
                Children.Add(fileDirs[0], new Node
                {
                    NodeType = Type.File,
                    Name = fileDirs[0]
                });
                return;
            }

            var directory = fileDirs[0];
            fileDirs.RemoveAt(0);

            if (!Children.TryGetValue(directory, out var childNode))
            {
                childNode = new Node
                {
                    NodeType = Type.Directory,
                    Name = directory
                };
            }

            childNode.AddChild(fileDirs);

            Children[directory] = childNode;
        }

        public void RemoveChild(List<string> fileDirs)
        {
            if (Children == null)
            {
                return;
            }

            if (fileDirs.Count == 1)
            {
                var fileName = fileDirs[0];
                Children.Remove(fileName);
                return;
            }

            var directory = fileDirs[0];
            fileDirs.RemoveAt(0);

            if (Children.TryGetValue(directory, out var childNode))
            {
                childNode.RemoveChild(fileDirs);
            }
        }
    }

    private Node _root = new()
    {
        NodeType = Node.Type.Directory,
        Name = "root"
    };

    public List<string> Files { get; } = new();

    public void AppendFileName(string fileName)
    {
        _root.AddChild(fileName.Split('/').ToList());
        Files.Add(fileName);
    }

    public void RemoveFileName(string fileName)
    {
        _root.RemoveChild(fileName.Split('/').ToList());
        Files.Remove(fileName);
    }

    public override string ToString()
    {
        return ToString(1);
    }

    public string ToString(int indent)
    {
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

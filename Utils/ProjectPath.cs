using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EastwardLib.Utils;

public static class ProjectPath
{
    public static string Escape(string path)
    {
        return string.Join('/', path.Split('/').Select(segment =>
        {
            if (segment is "" or "." or "..")
            {
                throw new ArgumentException("Invalid project asset path: " + path);
            }

            var name = Regex.Replace(segment, "[%<>:\"\\\\|?*\\x00-\\x1f]", match => "%" + ((int)match.Value[0]).ToString("X2"));
            name = Regex.Replace(name, "[. ]+$", match => string.Concat(match.Value.Select(c => "%" + ((int)c).ToString("X2"))));
            if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            {
                name = "%" + ((int)name[0]).ToString("X2") + name[1..];
            }

            return name;
        }));
    }

    public static Dictionary<string, string> Map(IEnumerable<string> paths)
    {
        var names = paths.Distinct(StringComparer.Ordinal).ToArray();
        var prefixes = names.SelectMany(name => Enumerable.Range(1, name.Split('/').Length).Select(count => string.Join('/', name.Split('/').Take(count)))).Distinct(StringComparer.Ordinal);
        var collisions = prefixes.GroupBy(Escape, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).SelectMany(group => group).ToHashSet(StringComparer.Ordinal);
        return names.ToDictionary(name => name, name => string.Join('/', name.Split('/').Select((segment, index) =>
        {
            var prefix = string.Join('/', name.Split('/').Take(index + 1));
            return Escape(segment) + (collisions.Contains(prefix) ? "~" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)))[..8].ToLowerInvariant() : "");
        })), StringComparer.Ordinal);
    }
}

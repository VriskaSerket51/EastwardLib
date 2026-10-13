using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EastwardLib.Utils;

/// <summary>Read the generated FSM table and assignments without executing Lua.</summary>
public static class FsmSource
{
    public static JsonObject Parse(string script)
    {
        var position = 0;
        Require(script, ref position, @"return\s*\(\s*function\s*\(\s*\)");
        Require(script, ref position, @"nodelist\s*=");
        var states = LuaTable.ReadLiteral(script, ref position) as JsonObject ?? throw new InvalidDataException("Missing FSM state table");
        while (true)
        {
            LuaTable.Skip(script, ref position);
            if (position < script.Length && script[position] == ';')
            {
                position++;
                continue;
            }
            if (Match(script, ref position, @"return\s+nodelist\s+end\s*\)\s*\(\s*\)"))
            {
                LuaTable.Skip(script, ref position);
                if (position != script.Length)
                {
                    throw new InvalidDataException("Unexpected code after generated FSM");
                }

                return states;
            }
            Require(script, ref position, @"nodelist\s*\[");
            var name = LuaTable.ReadLiteral(script, ref position)?.GetValue<string>() ?? throw new InvalidDataException("Missing FSM state name");
            Require(script, ref position, @"\]\s*\.\s*");
            // Use an anchored match at the current cursor, not a search through arbitrary Lua code.
            var assignment = new Regex(@"\G(jump|next)\s*=", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Match(script, position);
            if (!assignment.Success)
            {
                throw new InvalidDataException("Unsupported generated FSM assignment");
            }

            position += assignment.Length;
            var value = LuaTable.ReadLiteral(script, ref position);
            var state = states[name] as JsonObject ?? throw new InvalidDataException("Missing FSM assignment target: " + name);
            state[assignment.Groups[1].Value] = value;
        }
    }

    private static bool Match(string script, ref int position, string pattern)
    {
        LuaTable.Skip(script, ref position);
        var match = new Regex(@"\G(?:" + pattern + @")", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Match(script, position);
        if (!match.Success)
        {
            return false;
        }

        position += match.Length;
        return true;
    }

    private static void Require(string script, ref int position, string pattern)
    {
        if (!Match(script, ref position, pattern))
        {
            throw new InvalidDataException("Unsupported generated FSM syntax at offset " + position);
        }
    }
}

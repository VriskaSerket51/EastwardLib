using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EastwardLib.Utils;

/// <summary>Read serialized Lua table literals without executing Lua or loading a native VM.</summary>
public static class LuaTable
{
    public static JsonNode? Parse(string text)
    {
        var reader = new Reader(text);
        reader.Skip();
        if (reader.Match("return"))
        {
            reader.Skip();
        }

        var value = reader.Value();
        reader.Skip();
        reader.Match(";");
        reader.Skip();
        if (!reader.End)
        {
            throw new InvalidDataException("Lua resource contains executable code rather than a table literal");
        }

        return value;
    }

    internal static JsonNode? ReadLiteral(string text, ref int position)
    {
        var reader = new Reader(text, position);
        var value = reader.Value();
        position = reader.Position;
        return value;
    }

    internal static void Skip(string text, ref int position)
    {
        var reader = new Reader(text, position);
        reader.Skip();
        position = reader.Position;
    }

    private sealed class Reader(string text, int position = 0)
    {
        private int _position = position;
        public int Position => _position;
        public bool End => _position == text.Length;
        public bool Match(string value)
        {
            if (!text.AsSpan(_position).StartsWith(value, StringComparison.Ordinal))
            {
                return false;
            }

            _position += value.Length;
            return true;
        }

        private void Require(string value)
        {
            Skip();
            if (!Match(value))
            {
                throw new InvalidDataException($"Expected '{value}' at Lua offset {_position}");
            }
        }

        public void Skip()
        {
            while (!End)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                    continue;
                }
                if (!Match("--"))
                {
                    return;
                }

                if (TryLongString(out _))
                {
                    continue;
                }

                while (!End && text[_position] != '\n')
                {
                    _position++;
                }
            }
        }

        private bool TryLongString(out string value)
        {
            value = "";
            if (End || text[_position] != '[')
            {
                return false;
            }

            var start = _position + 1;
            while (start < text.Length && text[start] == '=')
            {
                start++;
            }

            if (start == text.Length || text[start] != '[')
            {
                return false;
            }

            var ending = "]" + new string('=', start - _position - 1) + "]";
            var end = text.IndexOf(ending, start + 1, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException("Unclosed Lua long string");
            }

            value = text[(start + 1)..end].Replace("\r\n", "\n");
            if (value.StartsWith('\n'))
            {
                value = value[1..];
            }

            _position = end + ending.Length;
            return true;
        }

        public JsonNode? Value(int depth = 0)
        {
            if (depth > 128)
            {
                throw new InvalidDataException("Lua table nesting exceeds supported depth");
            }

            Skip();
            if (End)
            {
                throw new EndOfStreamException("Truncated Lua table");
            }

            if (TryLongString(out var longString))
            {
                return JsonValue.Create(longString);
            }

            if (text[_position] is '\'' or '"')
            {
                return JsonValue.Create(String());
            }

            if (Match("{"))
            {
                var result = new JsonObject();
                var index = 1;
                Skip();
                while (!Match("}"))
                {
                    string key;
                    JsonNode? value;
                    if (Match("["))
                    {
                        key = Value(depth + 1)?.ToString() ?? throw new InvalidDataException("Nil Lua table key");
                        Require("]");
                        Require("=");
                        value = Value(depth + 1);
                    }
                    else
                    {
                        var saved = _position;
                        var identifier = Identifier();
                        Skip();
                        if (identifier.Length > 0 && Match("="))
                        {
                            key = identifier;
                            value = Value(depth + 1);
                        }
                        else
                        {
                            _position = saved;
                            key = (index++).ToString(CultureInfo.InvariantCulture);
                            value = Value(depth + 1);
                        }
                    }
                    result[key] = value;
                    Skip();
                    if (!(Match(",") || Match(";")) && !text.AsSpan(_position).StartsWith("}"))
                    {
                        throw new InvalidDataException("Expected Lua table separator");
                    }

                    Skip();
                }
                return result;
            }
            if (Match("true"))
            {
                return JsonValue.Create(true);
            }

            if (Match("false"))
            {
                return JsonValue.Create(false);
            }

            if (Match("nil"))
            {
                return null;
            }

            var begin = _position;
            while (!End && (char.IsDigit(text[_position]) || "+-.eE".Contains(text[_position])))
            {
                _position++;
            }

            if (begin == _position || !double.TryParse(text[begin.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                throw new InvalidDataException($"Unsupported Lua expression at offset {begin}");
            }

            return JsonValue.Create(number);
        }

        private string Identifier()
        {
            var start = _position;
            if (!End && (char.IsLetter(text[_position]) || text[_position] == '_'))
            {
                _position++;
                while (!End && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
                {
                    _position++;
                }
            }
            return text[start.._position];
        }

        private string String()
        {
            var quote = text[_position++];
            using var bytes = new MemoryStream();
            while (!End && text[_position] != quote)
            {
                if (text[_position] != '\\')
                {
                    var start = _position;
                    while (!End && text[_position] != quote && text[_position] != '\\')
                    {
                        _position++;
                    }

                    bytes.Write(Encoding.UTF8.GetBytes(text[start.._position]));
                    continue;
                }
                _position++;
                if (End)
                {
                    throw new EndOfStreamException("Truncated Lua string escape");
                }

                var escape = text[_position++];
                if (char.IsAsciiDigit(escape))
                {
                    var number = escape - '0';
                    for (var i = 0; i < 2 && !End && char.IsAsciiDigit(text[_position]); i++)
                    {
                        number = number * 10 + text[_position++] - '0';
                    }

                    if (number > 255)
                    {
                        throw new InvalidDataException("Invalid Lua decimal escape");
                    }

                    bytes.WriteByte((byte)number);
                }
                else if (escape == 'x')
                {
                    bytes.WriteByte(byte.Parse(text.Substring(_position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _position += 2;
                }
                else if (escape == 'z')
                {
                    while (!End && char.IsWhiteSpace(text[_position]))
                    {
                        _position++;
                    }
                }
                else
                {
                    if (escape == '\r' && !End && text[_position] == '\n')
                    {
                        _position++;
                    }

                    bytes.WriteByte(escape switch
                    {
                        'a' => 7,
                        'b' => 8,
                        'f' => 12,
                        'n' => 10,
                        'r' => 13,
                        't' => 9,
                        'v' => 11,
                        '\r' => 10,
                        _ => (byte)escape
                    });
                }
            }
            if (End)
            {
                throw new EndOfStreamException("Unclosed Lua string");
            }

            _position++;
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
    }
}

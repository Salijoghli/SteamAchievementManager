using System.Text;

namespace SAM.App.Services;

/// <summary>
/// A node of Valve's text KeyValues ("VDF") format: an ordered list of key → value pairs where
/// each value is either a string or a nested node. Keys may repeat and differ only in case, so
/// lookups return every match.
/// </summary>
internal sealed class TextVdfNode
{
    public List<KeyValuePair<string, object>> Entries { get; } = [];

    /// <summary>All child nodes whose key matches <paramref name="key"/> case-insensitively.</summary>
    public IEnumerable<TextVdfNode> Nodes(string key)
    {
        foreach (var (k, v) in this.Entries)
        {
            if (v is TextVdfNode node && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    /// <summary>The first string value whose key matches <paramref name="key"/> case-insensitively.</summary>
    public string? Value(string key)
    {
        foreach (var (k, v) in this.Entries)
        {
            if (v is string value && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Every node reached by following <paramref name="path"/>, matching each key case-insensitively.</summary>
    public IEnumerable<TextVdfNode> Path(params string[] path)
    {
        IEnumerable<TextVdfNode> current = [this];
        foreach (var key in path)
        {
            var k = key;
            current = current.SelectMany(n => n.Nodes(k));
        }

        return current;
    }
}

/// <summary>
/// Tolerant parser for text VDF: quoted or bare tokens, nested braces, backslash escapes inside
/// quotes, // comments and [$CONDITION] suffixes (ignored). Unbalanced input is closed implicitly.
/// </summary>
internal static class TextVdf
{
    public static TextVdfNode ParseFile(string path)
    {
        string text;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        return Parse(text);
    }

    public static TextVdfNode Parse(string text)
    {
        var root = new TextVdfNode();
        var stack = new Stack<TextVdfNode>();
        stack.Push(root);

        var position = 0;
        string? pendingKey = null;

        while (NextToken(text, ref position, out var token, out var kind))
        {
            var current = stack.Peek();
            switch (kind)
            {
                case TokenKind.Open:
                    var child = new TextVdfNode();
                    current.Entries.Add(new(pendingKey ?? "", child));
                    pendingKey = null;
                    stack.Push(child);
                    break;

                case TokenKind.Close:
                    pendingKey = null;
                    if (stack.Count > 1)
                    {
                        stack.Pop();
                    }

                    break;

                case TokenKind.Condition:
                    break;

                case TokenKind.String:
                    if (pendingKey == null)
                    {
                        pendingKey = token;
                    }
                    else
                    {
                        current.Entries.Add(new(pendingKey, token));
                        pendingKey = null;
                    }

                    break;
            }
        }

        return root;
    }

    private enum TokenKind
    {
        String,
        Open,
        Close,
        Condition,
    }

    private static bool NextToken(string text, ref int position, out string token, out TokenKind kind)
    {
        token = "";
        kind = TokenKind.String;

        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                var end = text.IndexOf('\n', position);
                position = end < 0 ? text.Length : end + 1;
                continue;
            }

            break;
        }

        if (position >= text.Length)
        {
            return false;
        }

        switch (text[position])
        {
            case '{':
                position++;
                kind = TokenKind.Open;
                return true;

            case '}':
                position++;
                kind = TokenKind.Close;
                return true;

            case '[':
            {
                var end = text.IndexOf(']', position);
                position = end < 0 ? text.Length : end + 1;
                kind = TokenKind.Condition;
                return true;
            }

            case '"':
            {
                position++;
                var builder = new StringBuilder();
                while (position < text.Length && text[position] != '"')
                {
                    var c = text[position++];
                    if (c == '\\' && position < text.Length)
                    {
                        var escaped = text[position++];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => escaped,
                        });
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                position++; // closing quote (or past the end)
                token = builder.ToString();
                return true;
            }

            default:
            {
                var start = position;
                while (position < text.Length &&
                    char.IsWhiteSpace(text[position]) == false &&
                    text[position] is not ('{' or '}' or '"'))
                {
                    position++;
                }

                token = text[start..position];
                return true;
            }
        }
    }
}

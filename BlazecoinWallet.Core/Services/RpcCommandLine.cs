using System.Text;
using System.Text.Json;

namespace BlazecoinWallet.Core.Services;

/// <summary>A Console line that cannot be turned into a command: an unclosed quote or bracket, or a
/// list/object that is not valid JSON. The message is shown to the user as typed.</summary>
public sealed class RpcCommandLineException(string message) : Exception(message);

/// <summary>
/// Turns the line typed into the Console page into an RPC method and its typed arguments.
/// <para>Until 2026-11-12 the page split on spaces and threw every double quote away, so a JSON list
/// could not be typed at all (<c>sendall ["B…"]</c> reached the node as the text <c>[B…]</c>), an empty
/// <c>""</c> argument vanished and shifted the ones after it, and a quoted number such as a passphrase
/// of digits was sent as a number. The rules now:</para>
/// <list type="bullet">
/// <item>Arguments are separated by spaces.</item>
/// <item><c>"double quotes"</c> make a string, always: <c>"123"</c> is the text 123 and <c>""</c> is an
/// empty argument. Inside them <c>\"</c> is a quote and <c>\\</c> a backslash; any other backslash is
/// kept, so a Windows path can be typed as it is.</item>
/// <item>An argument that starts with <c>[</c> or <c>{</c> is JSON and runs to its matching bracket,
/// spaces and quotes included. Invalid JSON is an error, never silently a string.</item>
/// <item><c>'single quotes'</c> take their content as it stands, the way a command copied from the
/// node's own help is written: <c>'["B…"]'</c> is JSON, anything else is a string.</item>
/// <item>A bare word is a number, <c>true</c>, <c>false</c> or <c>null</c> when it reads as one, and a
/// string otherwise (an address, a txid, a label).</item>
/// </list>
/// </summary>
public static class RpcCommandLine
{
    /// <summary>The method and its arguments: a <see cref="string"/> for text, a
    /// <see cref="JsonElement"/> for everything typed.</summary>
    public static (string Method, object[] Args) Parse(string line)
    {
        var tokens = Tokenize(line ?? string.Empty);
        if (tokens.Count == 0) throw new RpcCommandLineException("Type a command.");

        var first = tokens[0];
        if (first.Kind == TokenKind.Json || first.Text.Length == 0)
            throw new RpcCommandLineException("A command starts with the name of an RPC method, for example getblockchaininfo.");

        var args = new object[tokens.Count - 1];
        for (var i = 1; i < tokens.Count; i++) args[i - 1] = ToArgument(tokens[i], i);
        return (first.Text, args);
    }

    private enum TokenKind { Bare, Text, Json }

    private readonly record struct Token(string Text, TokenKind Kind);

    private static object ToArgument(Token token, int position)
    {
        switch (token.Kind)
        {
            case TokenKind.Text:
                return token.Text;

            case TokenKind.Json:
                try { return JsonSerializer.Deserialize<JsonElement>(token.Text); }
                catch (JsonException)
                {
                    throw new RpcCommandLineException(
                        $"Argument {position} is not valid JSON: {token.Text}\nInside a list or object, text needs double quotes, for example [\"B…\"].");
                }

            default:
                try
                {
                    var value = JsonSerializer.Deserialize<JsonElement>(token.Text);
                    if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                        return value;
                }
                catch (JsonException) { /* not a number or a keyword: plain text */ }
                return token.Text;
        }
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var started = false;
        var kind = TokenKind.Bare;

        void Flush()
        {
            if (!started) return;
            tokens.Add(new Token(sb.ToString(), kind));
            sb.Clear();
            started = false;
            kind = TokenKind.Bare;
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];

            if (char.IsWhiteSpace(c)) { Flush(); i++; continue; }

            if (c == '"')
            {
                i = ReadDoubleQuoted(s, i, sb);
                // Quoted text glued to a JSON argument leaves it JSON, and the parse then reports it.
                if (kind != TokenKind.Json) kind = TokenKind.Text;
                started = true;
                continue;
            }

            if (c == '\'')
            {
                var close = s.IndexOf('\'', i + 1);
                if (close < 0) throw new RpcCommandLineException("A single quote is opened and never closed.");
                var content = s.Substring(i + 1, close - i - 1);
                var trimmed = content.TrimStart();
                var json = !started && (trimmed.StartsWith('[') || trimmed.StartsWith('{'));
                sb.Append(content);
                if (json) kind = TokenKind.Json;
                else if (kind != TokenKind.Json) kind = TokenKind.Text;
                started = true;
                i = close + 1;
                continue;
            }

            if (!started && (c == '[' || c == '{'))
            {
                i = ReadJson(s, i, sb);
                kind = TokenKind.Json;
                started = true;
                continue;
            }

            sb.Append(c);
            started = true;
            i++;
        }

        Flush();
        return tokens;
    }

    /// <summary>Reads from the opening quote at <paramref name="open"/>; returns the index after the closing one.</summary>
    private static int ReadDoubleQuoted(string s, int open, StringBuilder sb)
    {
        var i = open + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
            {
                sb.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '"') return i + 1;
            sb.Append(c);
            i++;
        }
        throw new RpcCommandLineException("A double quote is opened and never closed.");
    }

    /// <summary>Copies a JSON list or object verbatim, from its opening bracket to the matching close;
    /// returns the index after it. Brackets inside JSON strings do not count.</summary>
    private static int ReadJson(string s, int open, StringBuilder sb)
    {
        var depth = 0;
        var inString = false;
        var i = open;
        while (i < s.Length)
        {
            var c = s[i];
            sb.Append(c);
            i++;

            if (inString)
            {
                if (c == '\\' && i < s.Length) { sb.Append(s[i]); i++; }
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        throw new RpcCommandLineException(
            inString ? "A double quote inside a list or object is never closed." : "A list or object is opened and never closed.");
    }
}

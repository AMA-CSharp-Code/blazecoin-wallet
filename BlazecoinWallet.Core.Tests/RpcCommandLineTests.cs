using System.Text.Json;
using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The Console page's line parser (2026-11-12). The page used to drop every double quote, so a JSON
/// list could not be typed (<c>sendall</c> was unusable), an empty <c>""</c> argument disappeared, and
/// a quoted number went to the node as a number.
/// </summary>
public class RpcCommandLineTests
{
    private const string Addr = "BExampleAddressForParserTests000000";

    private static string Json(object arg) => Assert.IsType<JsonElement>(arg).GetRawText();

    [Fact]
    public void A_bare_command_has_no_arguments()
    {
        var (method, args) = RpcCommandLine.Parse("  getblockchaininfo  ");
        Assert.Equal("getblockchaininfo", method);
        Assert.Empty(args);
    }

    [Fact]
    public void Bare_words_are_numbers_and_keywords_when_they_read_as_one_and_text_otherwise()
    {
        var (method, args) = RpcCommandLine.Parse($"sendtoaddress {Addr} 20000.5 true null");
        Assert.Equal("sendtoaddress", method);
        Assert.Equal(Addr, args[0]);
        Assert.Equal(20000.5m, Assert.IsType<JsonElement>(args[1]).GetDecimal());
        Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(args[2]).ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonElement>(args[3]).ValueKind);
    }

    [Fact]
    public void A_list_typed_with_its_quotes_reaches_the_node_as_a_list()
    {
        // The line that could not be typed before: the quotes were stripped and the node got "[B…]".
        var (method, args) = RpcCommandLine.Parse($"sendall [\"{Addr}\"]");
        Assert.Equal("sendall", method);
        var list = Assert.IsType<JsonElement>(Assert.Single(args));
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(Addr, list[0].GetString());
    }

    [Fact]
    public void A_list_or_object_may_contain_spaces_and_brackets_inside_its_strings()
    {
        var (_, args) = RpcCommandLine.Parse("""createrawtransaction [ {"txid": "ab", "vout": 0} ] {"B addr ]": 1.5} 0""");
        Assert.Equal(3, args.Length);
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(args[0]).ValueKind);
        Assert.Equal(1.5m, Assert.IsType<JsonElement>(args[1]).GetProperty("B addr ]").GetDecimal());
        Assert.Equal("0", Json(args[2]));
    }

    [Fact]
    public void Single_quotes_as_in_the_nodes_own_help_carry_json()
    {
        var (_, args) = RpcCommandLine.Parse($"sendall '[\"{Addr}\"]' null 'a label'");
        Assert.Equal(Addr, Assert.IsType<JsonElement>(args[0])[0].GetString());
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonElement>(args[1]).ValueKind);
        Assert.Equal("a label", args[2]);
    }

    [Fact]
    public void Double_quotes_always_make_text_even_around_digits()
    {
        // A passphrase made of digits used to be sent as a number, which the node refuses.
        var (_, args) = RpcCommandLine.Parse("walletpassphrase \"12345678\" 60");
        Assert.Equal("12345678", args[0]);
        Assert.Equal("60", Json(args[1]));
    }

    [Fact]
    public void An_empty_quoted_argument_keeps_its_place()
    {
        // sendtoaddress address amount comment comment_to subtractfeefromamount
        var (_, args) = RpcCommandLine.Parse($"sendtoaddress {Addr} 5 \"\" \"\" true");
        Assert.Equal(5, args.Length);
        Assert.Equal(string.Empty, args[2]);
        Assert.Equal(string.Empty, args[3]);
        Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(args[4]).ValueKind);
    }

    [Fact]
    public void Quoted_text_keeps_spaces_escaped_quotes_and_windows_paths()
    {
        var (_, args) = RpcCommandLine.Parse("""signmessage "two words" "say \"hi\"" "C:\Users\me\wallet.dat" "a\\b" """);
        Assert.Equal("two words", args[0]);
        Assert.Equal("say \"hi\"", args[1]);
        Assert.Equal(@"C:\Users\me\wallet.dat", args[2]);
        Assert.Equal(@"a\b", args[3]);
    }

    [Fact]
    public void The_website_s_signmessage_line_still_parses_as_two_strings()
    {
        var (method, args) = RpcCommandLine.Parse($"signmessage \"{Addr}\" \"blazecoin-verify:00000000\"");
        Assert.Equal("signmessage", method);
        Assert.Equal([Addr, "blazecoin-verify:00000000"], args);
    }

    [Theory]
    [InlineData("getnewaddress \"label", "double quote")]
    [InlineData("getnewaddress 'label", "single quote")]
    [InlineData("sendall [\"B1\"", "never closed")]
    [InlineData("sendall [\"B1]", "never closed")]
    public void An_unclosed_quote_or_bracket_is_refused_before_anything_is_sent(string line, string expected)
    {
        var ex = Assert.Throws<RpcCommandLineException>(() => RpcCommandLine.Parse(line));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_list_that_is_not_valid_json_is_refused_and_never_sent_as_text()
    {
        // What the old parser produced for every list: the node then answered with a type error.
        var ex = Assert.Throws<RpcCommandLineException>(() => RpcCommandLine.Parse($"sendall [{Addr}]"));
        Assert.Contains("Argument 1 is not valid JSON", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1] getbalance")]
    [InlineData("\"\" getbalance")]
    public void A_line_without_a_method_name_is_refused(string line)
        => Assert.Throws<RpcCommandLineException>(() => RpcCommandLine.Parse(line));
}

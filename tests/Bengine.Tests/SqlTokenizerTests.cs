namespace Bengine.Tests;

using System.Linq;
using Bengine.Parsing;

public class SqlTokenizerTests
{
    [Fact]
    public void Tokenizes_words_numbers_and_punctuation()
    {
        var tokens = SqlTokenizer.Tokenize("id INT (10),");

        Assert.Collection(
            tokens,
            t => Assert.Equal((SqlTokenKind.Word, "id"), (t.Kind, t.Text)),
            t => Assert.Equal((SqlTokenKind.Word, "INT"), (t.Kind, t.Text)),
            t => Assert.Equal((SqlTokenKind.Punct, "("), (t.Kind, t.Text)),
            t => Assert.Equal((SqlTokenKind.Number, "10"), (t.Kind, t.Text)),
            t => Assert.Equal((SqlTokenKind.Punct, ")"), (t.Kind, t.Text)),
            t => Assert.Equal((SqlTokenKind.Punct, ","), (t.Kind, t.Text)));
    }

    [Theory]
    [InlineData("\"col\"", '"')]
    [InlineData("`col`", '`')]
    [InlineData("[col]", '[')]
    public void Reads_delimited_identifiers_of_every_style(string sql, char quote)
    {
        var token = SqlTokenizer.Tokenize(sql).Single();

        Assert.Equal(SqlTokenKind.Quoted, token.Kind);
        Assert.Equal("col", token.Text);
        Assert.Equal(quote, token.Quote);
    }

    [Fact]
    public void Unescapes_doubled_delimiters_in_identifiers()
    {
        var token = SqlTokenizer.Tokenize("\"a\"\"b\"").Single();

        Assert.Equal("a\"b", token.Text);
    }

    [Fact]
    public void Reads_string_literals_retaining_quotes()
    {
        var token = SqlTokenizer.Tokenize("'pending'").Single();

        Assert.Equal(SqlTokenKind.String, token.Kind);
        Assert.Equal("'pending'", token.Text);
    }

    [Fact]
    public void Handles_escaped_and_doubled_quotes_in_strings()
    {
        Assert.Single(SqlTokenizer.Tokenize("'O''Brien'"));
        Assert.Single(SqlTokenizer.Tokenize("'a\\'b'"));
    }

    [Theory]
    [InlineData("a -- comment\n b")]
    [InlineData("a /* block */ b")]
    [InlineData("a # mysql comment\n b")]
    public void Skips_comments(string sql)
    {
        var words = SqlTokenizer.Tokenize(sql).Where(t => t.Kind == SqlTokenKind.Word).ToList();

        Assert.Equal(new[] { "a", "b" }, words.Select(t => t.Text));
    }

    [Fact]
    public void Records_source_offsets()
    {
        var token = SqlTokenizer.Tokenize("  id").Last();

        Assert.Equal(2, token.Start);
        Assert.Equal(4, token.End);
    }
}

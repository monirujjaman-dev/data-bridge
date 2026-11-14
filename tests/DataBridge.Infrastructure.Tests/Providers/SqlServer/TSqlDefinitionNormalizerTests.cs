using DataBridge.Infrastructure.Providers.SqlServer.Schema;

namespace DataBridge.Infrastructure.Tests.Providers.SqlServer;

public sealed class TSqlDefinitionNormalizerTests
{
    private static string Normalize(string sql) => TSqlDefinitionNormalizer.Instance.Normalize(sql);

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1", "CREATE VIEW v AS SELECT 1")]
    [InlineData("  CREATE VIEW v AS SELECT 1  \r\n", "CREATE VIEW v AS SELECT 1")]
    [InlineData("CREATE VIEW v\r\nAS\r\n    SELECT 1", "CREATE VIEW v\nAS\nSELECT 1")]
    [InlineData("CREATE VIEW v\n\n\tAS   SELECT\t1", "CREATE VIEW v\nAS SELECT 1")]
    public void Whitespace_outside_literals_is_collapsed_but_line_breaks_are_kept(string sql, string expected) =>
        Assert.Equal(expected, Normalize(sql));

    [Fact]
    public void Line_endings_and_indentation_do_not_matter()
    {
        var windows = "CREATE PROCEDURE p\r\nAS\r\nBEGIN\r\n    SELECT 1;\r\nEND";
        var unix = "CREATE PROCEDURE p\nAS\nBEGIN\n\tSELECT 1;\nEND\n";

        Assert.Equal(Normalize(windows), Normalize(unix));
    }

    [Theory]
    [InlineData("SELECT 'a  b'", "SELECT 'a b'")]
    [InlineData("SELECT 'line\r\nbreak'", "SELECT 'line\nbreak'")]
    [InlineData("SELECT 'it''s  here'", "SELECT 'it''s here'")]
    [InlineData("SELECT N'x  y'", "SELECT N'x y'")]
    [InlineData("SELECT [a  b]", "SELECT [a b]")]
    [InlineData("SELECT [a]]  b]", "SELECT [a]] b]")]
    [InlineData("SELECT \"a  b\"", "SELECT \"a b\"")]
    public void Literals_and_quoted_identifiers_are_never_changed(string left, string right) =>
        Assert.NotEqual(Normalize(left), Normalize(right));

    [Fact]
    public void Literal_content_is_kept_exactly()
    {
        const string sql = "SELECT 'a  \r\n  b', [x  y], \"p  q\", 'it''s  ok'";

        Assert.Equal(sql, Normalize(sql));
    }

    [Fact]
    public void Line_comment_still_ends_at_a_line_break()
    {
        // If the break were collapsed to a space, SELECT 2 would become part of the comment.
        var normalized = Normalize("SELECT 1 -- note\r\n    SELECT 2");

        Assert.Equal("SELECT 1 -- note\nSELECT 2", normalized);
        Assert.NotEqual(Normalize("SELECT 1 -- note SELECT 2"), normalized);
    }

    [Fact]
    public void Comment_text_is_kept()
    {
        Assert.NotEqual(Normalize("SELECT 1 -- a  b"), Normalize("SELECT 1 -- a b"));
        Assert.NotEqual(Normalize("SELECT /* a  b */ 1"), Normalize("SELECT /* a b */ 1"));
    }

    [Fact]
    public void Block_comments_nest_and_only_their_line_endings_are_normalized()
    {
        Assert.Equal("SELECT /* a /* 'b */ c'\n */ 1", Normalize("SELECT /* a /* 'b */ c'\r\n */   1"));
    }

    [Theory]
    [InlineData("SELECT 'unterminated  ")]
    [InlineData("SELECT [unterminated  ")]
    [InlineData("SELECT /* unterminated  ")]
    public void Unterminated_tokens_are_kept_to_the_end(string sql) =>
        Assert.Equal(sql.TrimEnd(), Normalize(sql));

    [Fact]
    public void Case_is_never_changed() =>
        Assert.NotEqual(Normalize("select [Name] from t"), Normalize("SELECT [Name] FROM t"));
}

using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class FtsQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Build_NullEmptyOrWhitespace_ReturnsNull(string? input) =>
        Assert.Null(FtsQuery.Build(input));

    [Theory]
    [InlineData("-")]
    [InlineData("- + &")]
    [InlineData("*")]
    [InlineData("\"\"")]
    [InlineData("\" - \"")]
    [InlineData("OR")]
    [InlineData("OR OR")]
    [InlineData("( ) : ^")]
    public void Build_OnlyJunk_ReturnsNull(string input) =>
        Assert.Null(FtsQuery.Build(input));

    [Theory]
    [InlineData("budget", "\"budget\"")]
    [InlineData("budget review", "\"budget\" AND \"review\"")]
    [InlineData("  budget \t  review  ", "\"budget\" AND \"review\"")]
    [InlineData("budget-2026", "\"budget-2026\"")]
    [InlineData("C++", "\"C++\"")]
    [InlineData("O'Example", "\"O'Example\"")]
    [InlineData("budg*", "\"budg\"*")]
    [InlineData("budg**", "\"budg\"*")]
    [InlineData("budget-20*", "\"budget-20\"*")]
    [InlineData("*budg", "\"*budg\"")]
    [InlineData("bud*get", "\"bud*get\"")]
    [InlineData("a OR b", "(\"a\" OR \"b\")")]
    [InlineData("x a OR b OR c y", "\"x\" AND (\"a\" OR \"b\" OR \"c\") AND \"y\"")]
    [InlineData("OR budget", "\"budget\"")]
    [InlineData("budget OR", "\"budget\"")]
    [InlineData("a OR OR b", "(\"a\" OR \"b\")")]
    [InlineData("cats or dogs", "\"cats\" AND \"or\" AND \"dogs\"")]
    [InlineData("a OR - b", "(\"a\" OR \"b\")")]
    [InlineData("\"exact phrase\" memo", "\"exact phrase\" AND \"memo\"")]
    [InlineData("a \"OR\" b", "\"a\" AND \"OR\" AND \"b\"")]
    [InlineData("say \"hi", "\"say\" AND \"hi\"")]
    [InlineData("ab\"cd ef\"", "\"ab\" AND \"cd ef\"")]
    [InlineData("NOT secret", "\"NOT\" AND \"secret\"")]
    [InlineData("NEAR(a b)", "\"NEAR(a\" AND \"b)\"")]
    [InlineData("subject:budget", "\"subject:budget\"")]
    [InlineData("^start", "\"^start\"")]
    [InlineData("a\0b", "\"a\" AND \"b\"")]
    [InlineData("Müller Zürich", "\"Müller\" AND \"Zürich\"")]
    [InlineData("東京", "\"東京\"")]
    public void Build_ProducesExpected(string input, string expected) =>
        Assert.Equal(expected, FtsQuery.Build(input));

    private static long[] Match(string? userText)
    {
        var q = FtsQuery.Build(userText);
        if (q is null)
        {
            return [];
        }
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE t USING fts5(subject, body, tokenize='unicode61 remove_diacritics 2');";
            create.ExecuteNonQuery();
        }
        var rows = new (int Id, string Subject, string Body)[]
        {
            (1, "Budget 2026 review", "see budget-2026.xlsx"),
            (2, "C++ meeting", "with O'Example"),
            (3, "Invoice", "invoice from Müller"),
            (4, "Cats", "dogs and birds"),
        };
        foreach (var r in rows)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO t(rowid, subject, body) VALUES ($id, $s, $b)";
            ins.Parameters.AddWithValue("$id", r.Id);
            ins.Parameters.AddWithValue("$s", r.Subject);
            ins.Parameters.AddWithValue("$b", r.Body);
            ins.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM t WHERE t MATCH $q ORDER BY rowid";
        cmd.Parameters.AddWithValue("$q", q);
        var result = new List<long>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            result.Add(rd.GetInt64(0));
        }
        return result.ToArray();
    }

    [Theory]
    [InlineData("budget-2026")]
    [InlineData("C++")]
    [InlineData("O'Example")]
    [InlineData("\"")]
    [InlineData("\"\"\"")]
    [InlineData("a\"b")]
    [InlineData("*")]
    [InlineData("**a")]
    [InlineData("-a")]
    [InlineData("a -")]
    [InlineData("NOT")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NEAR(a, b)")]
    [InlineData("a:b")]
    [InlineData("{subject}: x")]
    [InlineData("( ( (")]
    [InlineData(") )")]
    [InlineData("^")]
    [InlineData("+")]
    [InlineData("'; DROP TABLE t; --")]
    [InlineData("\0")]
    [InlineData("\uD800")]
    [InlineData("a OR")]
    [InlineData("OR OR OR")]
    public void Fts_NeverThrows_OnHostileInput(string input) =>
        Assert.Null(Record.Exception(() => Match(input)));

    [Fact] public void Fts_Hyphenated_FindsRow() => Assert.Equal([1L], Match("budget-2026"));
    [Fact] public void Fts_CPlusPlus_FindsRow() => Assert.Equal([2L], Match("C++"));
    [Fact] public void Fts_Apostrophe_FindsRow() => Assert.Equal([2L], Match("O'Example"));

    [Fact]
    public void Fts_Prefix()
    {
        Assert.Equal([1L], Match("budg*"));
        Assert.Empty(Match("budg"));
    }

    [Fact]
    public void Fts_And()
    {
        Assert.Equal([1L], Match("budget review"));
        Assert.Empty(Match("budget invoice"));
    }

    [Fact] public void Fts_Or() => Assert.Equal([1L, 3L], Match("budget OR invoice"));
    [Fact] public void Fts_OrBindsTighter() => Assert.Equal([4L], Match("invoice OR cats dogs"));
    [Fact] public void Fts_Diacritics() => Assert.Equal([3L], Match("Muller"));

    [Fact]
    public void Fts_Phrase()
    {
        Assert.Empty(Match("\"budget review\""));
        Assert.Equal([1L], Match("\"2026 review\""));
    }
}

using System.Text.Json;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class IndexQueryToolTextTests : IndexQueryFixture
{
    [Theory]
    [InlineData("mail")]
    [InlineData("mail_fts")]
    [InlineData("mail_attachments")]
    public void Description_ListsEachViewWithItsRealColumns(string view)
    {
        var cols = Q($"SELECT * FROM {view} LIMIT 0").Columns;
        Assert.Contains($"{view}({string.Join(", ", cols)})", IndexQueryTexts.ToolDescription);
    }

    [Fact]
    public void Description_FtsColumnNumbers_MatchTheView()
    {
        var cols = Q("SELECT * FROM mail_fts LIMIT 0").Columns.Skip(3).ToArray();
        Assert.Equal("subject", cols[0]);
        Assert.Equal("attachment_names", cols[^1]);
        Assert.Contains(string.Join(", ", cols.Select((c, i) => $"{i} {c}")), IndexQueryTexts.ToolDescription);
    }

    [Fact]
    public void Description_StatesLimitsAndPointers()
    {
        foreach (var s in new[] { "10 s", "at most 1000 rows", "2,000 characters", "mailbox=\"all\"", "email_stats", "get_message", "MATCH", "ORDER BY rank", "PRAGMA" })
        {
            Assert.Contains(s, IndexQueryTexts.ToolDescription);
        }
    }

    [Fact]
    public void Examples_AreInTheDescription_InOrder()
    {
        Assert.Equal(4, IndexQueryTexts.Examples.Count);
        string[] labels = ["Top senders last year: ", "Mail per month: ", "Full text with snippets: ", "PDFs by sender: "];
        var last = -1;
        for (var i = 0; i < 4; i++)
        {
            var d = IndexQueryTexts.ToolDescription;
            var idx = d.IndexOf(IndexQueryTexts.Examples[i], StringComparison.Ordinal);
            Assert.True(idx > last, "example " + i + " out of order");
            Assert.Equal(labels[i], d.Substring(idx - labels[i].Length, labels[i].Length));
            last = idx;
        }
    }

    [Fact]
    public void Examples_RunOnTheIndex()
    {
        SeedOwn(DateTimeOffset.UtcNow.AddDays(-30).AddHours(-5));
        var top = Q(IndexQueryTexts.ExampleTopSenders);
        Assert.Equal(2, top.RowCount);
        Assert.All(top.Rows, r => Assert.Equal(1L, r[1]));
        Assert.Equal(["alice@example.com", "bob@contoso.com"], top.Rows.Select(r => Str(r[0])).Order().ToArray());

        var months = Q(IndexQueryTexts.ExampleMailPerMonth);
        Assert.NotEmpty(months.Rows);
        Assert.All(months.Rows, r => Assert.Matches(@"^\d{4}-\d\d$", Str(r[0])));

        var fts = Q(IndexQueryTexts.ExampleFullText);
        Assert.Equal(["o1"], fts.Rows.Select(r => Str(r[0])).ToArray());
        Assert.Contains("**budget**", Str(fts.Rows[0][3]), StringComparison.OrdinalIgnoreCase);

        var pdf = Q(IndexQueryTexts.ExamplePdfsBySender);
        var row = Assert.Single(pdf.Rows);
        Assert.Equal("bob@contoso.com", row[0]);
        Assert.Equal(1L, row[1]);

        Assert.All(new[] { top, months, fts, pdf }, r => Assert.False(r.Truncated));
    }

    [Fact]
    public void Examples_RunWithAll()
    {
        foreach (var sql in IndexQueryTexts.Examples)
        {
            var r = Q(sql, "all");
            Assert.Equal("mailbox", r.Columns[0]);
            Assert.All(r.Rows, row => Assert.Contains(Str(row[0]), new[] { "", Shared, OBrien }));
        }
    }

    [Fact]
    public void Result_SerializesAsSpecified()
    {
        var o = McpJsonUtilities.DefaultOptions;
        var r = new IndexQueryResult(["a", "n", "x", "z"], [["s", 143L, 1.5, null]], 1, false, null, 0, 42);
        Assert.Equal("""{"columns":["a","n","x","z"],"rows":[["s",143,1.5,null]],"row_count":1,"truncated":false,"truncated_cells":0,"elapsed_ms":42}""",
            JsonSerializer.Serialize(r, o));
        var t = JsonSerializer.Serialize(r with { Truncated = true, TruncatedReason = "max_rows" }, o);
        Assert.Contains("\"truncated\":true,\"truncated_reason\":\"max_rows\",\"truncated_cells\":", t);
    }
}
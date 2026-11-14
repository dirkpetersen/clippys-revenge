using SQLitePCL;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class IndexQueryAuthorizerTests
{
    private static readonly string[] DirectColumns =
        ["id", "subject", "body", "sender_name", "sender_email", "to_addrs", "cc_addrs", "attachment_names"];

    private static IndexQueryAuthorizer.Decision D(int action, string? a1 = null, string? a2 = null,
        string? db = "main", string? view = null) => IndexQueryAuthorizer.Decide(action, a1, a2, db, view);

    private static void Allow(IndexQueryAuthorizer.Decision d)
    {
        Assert.True(d.Allow, "denied: " + d.What);
        Assert.Null(d.What);
    }

    private static void Deny(string what, IndexQueryAuthorizer.Decision d)
    {
        Assert.False(d.Allow);
        Assert.Equal(what, d.What);
    }

    [Fact]
    public void Allowed_Cases()
    {
        Allow(D(raw.SQLITE_SELECT, null, null, null, null));
        Allow(D(raw.SQLITE_RECURSIVE, null, null, null, null));
        Allow(D(raw.SQLITE_READ, "emails", "entry_id", "main", "mail"));
        Allow(D(raw.SQLITE_READ, "emails", "conversation_topic", "main", "mail"));
        Allow(D(raw.SQLITE_READ, "emails", "", "main", "mail"));
        Allow(D(raw.SQLITE_READ, "attachments", "filename", "main", "mail_attachments"));
        Allow(D(raw.SQLITE_READ, "emails_fts", "rank", "main", "mail_fts"));
        Allow(D(raw.SQLITE_READ, "emails_fts", "emails_fts", "main", null));
        foreach (var t in new[] { "emails_fts_data", "emails_fts_idx", "emails_fts_config", "emails_fts_docsize" })
            Allow(D(raw.SQLITE_READ, t, "anything", "main", null));
        foreach (var c in DirectColumns) Allow(D(raw.SQLITE_READ, "emails", c, "main", null));
        Allow(D(raw.SQLITE_READ, "mail", "subject", "temp", null));
        Allow(D(raw.SQLITE_READ, "json_each", "value", "main", null));
        foreach (var f in new[] { "snippet", "bm25", "highlight", "count", "strftime", "sqlite_version", "SQLITE_VERSION" })
            Allow(D(raw.SQLITE_FUNCTION, null, f));
        Allow(D(raw.SQLITE_PRAGMA, "data_version", null, "main", null));
        // No-column reads: a CTE name, and the views' own "FROM main.emails" once flattened.
        Allow(D(raw.SQLITE_READ, "r", "", null, null));
        Allow(D(raw.SQLITE_READ, "emails", "", "main", null));
        Allow(D(raw.SQLITE_READ, "attachments", "", "main", null));
    }

    [Theory]
    [InlineData("meta")]
    [InlineData("sync_state")]
    [InlineData("ai_decisions")]
    [InlineData("sqlite_master")]
    [InlineData("pragma_table_info")]
    [InlineData("dbstat")]
    public void NoColumnRead_OfHiddenTable_Denied(string table) =>
        Deny("table " + table, D(raw.SQLITE_READ, table, "", null, null));

    [Theory]
    [InlineData("meta")]
    [InlineData("sync_state")]
    [InlineData("ai_decisions")]
    [InlineData("sqlite_master")]
    [InlineData("sqlite_schema")]
    [InlineData("sqlite_temp_master")]
    [InlineData("pragma_table_info")]
    public void HiddenTables_Denied(string table) =>
        Deny("table " + table, D(raw.SQLITE_READ, table, "value", "main", null));

    [Fact]
    public void Attachments_TopLevel_Denied() =>
        Deny("table attachments", D(raw.SQLITE_READ, "attachments", "", null, null));

    [Theory]
    [InlineData("entry_id")]
    [InlineData("store_id")]
    [InlineData("internet_message_id")]
    [InlineData("in_reply_to")]
    [InlineData("clippy_marker")]
    [InlineData("folder")]
    [InlineData("event_time")]
    [InlineData("last_modified")]
    public void HiddenEmailColumns_Denied(string column) =>
        Deny("column emails." + column, D(raw.SQLITE_READ, "emails", column, "main", null));

    [Fact]
    public void EmailsWholeTable_Denied() =>
        Deny("table emails", D(raw.SQLITE_READ, "emails", "", null, null));

    [Fact]
    public void ThroughViewContext_Denied()
    {
        Deny("column emails.store_id", D(raw.SQLITE_READ, "emails", "store_id", "main", "mail"));
        Deny("table meta", D(raw.SQLITE_READ, "meta", "key", "main", "mail"));
        Deny("table ai_decisions", D(raw.SQLITE_READ, "ai_decisions", "outcome", "main", "mail_fts"));
        Deny("column attachments.rowid", D(raw.SQLITE_READ, "attachments", "rowid", "main", "mail_attachments"));
    }

    [Theory]
    [InlineData("load_extension")]
    [InlineData("LOAD_EXTENSION")]
    public void LoadExtension_Denied(string name) =>
        Deny("function load_extension", D(raw.SQLITE_FUNCTION, null, name));

    [Theory]
    [InlineData("fts3_tokenizer")]
    [InlineData("readfile")]
    [InlineData("writefile")]
    [InlineData("edit")]
    [InlineData("sqlite_compileoption_get")]
    [InlineData("sqlite_offset")]
    public void DeniedFunctions(string name) =>
        Deny("function " + name, D(raw.SQLITE_FUNCTION, null, name));

    [Fact]
    public void UnknownFunction_Denied() =>
        Deny("function (unknown)", D(raw.SQLITE_FUNCTION, null, null));

    [Fact]
    public void Pragmas_Denied()
    {
        Deny("PRAGMA", D(raw.SQLITE_PRAGMA, "table_info", "emails"));
        Deny("PRAGMA", D(raw.SQLITE_PRAGMA, "query_only", "0"));
        Deny("PRAGMA", D(raw.SQLITE_PRAGMA, "data_version", "1"));
        Deny("PRAGMA", D(raw.SQLITE_PRAGMA, "data_version", null, "main", "mail"));
    }

    [Fact]
    public void AttachDetachTransactions_Denied()
    {
        Deny("ATTACH", D(raw.SQLITE_ATTACH));
        Deny("DETACH", D(raw.SQLITE_DETACH));
        Deny("transactions", D(raw.SQLITE_TRANSACTION));
        Deny("transactions", D(raw.SQLITE_SAVEPOINT));
    }

    [Fact]
    public void WriteActions_Denied()
    {
        var codes = Enumerable.Range(1, 18).Concat([23]).Concat(Enumerable.Range(26, 5));
        foreach (var code in codes) Deny("writing", D(code));
    }

    [Theory]
    [InlineData(0, "action 0")]
    [InlineData(34, "action 34")]
    [InlineData(-1, "action -1")]
    public void UnknownActions_Denied(int code, string what) => Deny(what, D(code));

    [Theory]
    [InlineData("  select 1", "SELECT", 2)]
    [InlineData("/* c */ -- x\n WITH a AS (SELECT 1) SELECT * FROM a", "WITH", 14)]
    [InlineData(";;  VALUES (1)", "VALUES", 4)]
    [InlineData("/* unterminated", "", 15)]
    [InlineData("-- only", "", 7)]
    [InlineData("(SELECT 1)", "", 0)]
    [InlineData("PRAGMA x", "PRAGMA", 0)]
    [InlineData("delete from mail", "DELETE", 0)]
    public void LeadingKeyword_Cases(string sql, string keyword, int start)
    {
        var (k, s) = IndexQuery.LeadingKeyword(sql);
        Assert.Equal(keyword, k);
        Assert.Equal(start, s);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ; ;\n", true)]
    [InlineData("-- c", true)]
    [InlineData("/* c */;", true)]
    [InlineData("; SELECT 2", false)]
    [InlineData("x", false)]
    [InlineData(";/**/ DROP TABLE emails", false)]
    public void IsEmptyTail_Cases(string? tail, bool expected) =>
        Assert.Equal(expected, IndexQuery.IsEmptyTail(tail));
}

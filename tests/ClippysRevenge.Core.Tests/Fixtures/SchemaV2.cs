using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

/// <summary>A frozen copy of the schema-2 DDL, so later changes to EmailIndex can't change the migration fixture.</summary>
internal static class SchemaV2
{
    public const string Step2Ddl = """
        CREATE INDEX ix_emails_irt ON emails(in_reply_to);
        CREATE INDEX ix_emails_topic ON emails(conversation_topic COLLATE NOCASE, event_time);

        CREATE TABLE ai_decisions (
          source_entry_id TEXT PRIMARY KEY,
          source_imid TEXT,
          conversation_topic TEXT,
          decided INTEGER NOT NULL,
          outcome TEXT NOT NULL,
          rule TEXT,
          model_calls INTEGER NOT NULL DEFAULT 0,
          attempts INTEGER NOT NULL DEFAULT 0,
          confidence REAL,
          draft_entry_id TEXT,
          draft_created INTEGER);
        CREATE INDEX ix_ai_decided ON ai_decisions(decided);
        CREATE INDEX ix_ai_imid ON ai_decisions(source_imid);
        """;

    public static void Create(string dbPath)
    {
        SchemaV1.Create(dbPath, SchemaV1.Sample());
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        foreach (var name in new[] { "in_reply_to", "conversation_topic", "clippy_marker" })
            c.Execute("ALTER TABLE emails ADD COLUMN " + name + " TEXT", transaction: tx);
        c.Execute(Step2Ddl, transaction: tx);
        c.Execute("UPDATE meta SET value='2' WHERE key='schema_version'", transaction: tx);
        c.Execute("INSERT INTO meta(key, value) VALUES ('thread_meta_complete', '1')", transaction: tx);
        c.Execute("UPDATE emails SET conversation_topic='Budget' WHERE entry_id IN ('A1','S1')", transaction: tx);
        c.Execute("INSERT INTO ai_decisions VALUES ('A1', '<a1@x>', NULL, 1763000700, 'drafted', NULL, 1, 0, 0.9, 'D1', 1763000700)",
            transaction: tx);
        c.Execute("INSERT INTO ai_decisions VALUES ('A2', '<a2@x>', NULL, 1763000800, 'no_reply', NULL, 1, 0, 0.2, NULL, NULL)",
            transaction: tx);
        tx.Commit();
        c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }
}

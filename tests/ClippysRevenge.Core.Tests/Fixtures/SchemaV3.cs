using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

/// <summary>A frozen copy of the schema-3 DDL on top of <see cref="SchemaV2"/>, so later changes to EmailIndex can't change the migration fixture.</summary>
internal static class SchemaV3
{
    public const string Step3Ddl = """
        CREATE TABLE ai_drafts (
          draft_entry_id TEXT PRIMARY KEY,
          source_entry_id TEXT NOT NULL,
          created INTEGER NOT NULL,
          fingerprint TEXT NOT NULL,
          checked INTEGER NOT NULL,
          state TEXT NOT NULL,
          attempts INTEGER NOT NULL DEFAULT 0,
          updated INTEGER NOT NULL);
        CREATE INDEX ix_ai_drafts_state ON ai_drafts(state, created);
        CREATE TABLE ai_revisits (
          id INTEGER PRIMARY KEY,
          draft_entry_id TEXT NOT NULL,
          decided INTEGER NOT NULL,
          outcome TEXT NOT NULL,
          model_calls INTEGER NOT NULL);
        CREATE INDEX ix_ai_revisits_decided ON ai_revisits(decided);
        """;

    public static void Create(string dbPath)
    {
        SchemaV2.Create(dbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        c.Execute(Step3Ddl, transaction: tx);
        c.Execute("INSERT INTO ai_drafts VALUES ('D1', 'A1', 1763000700, @fp, 1763000700, 'open', 0, 1763000800)",
            new { fp = new string('a', 64) }, tx);
        c.Execute("INSERT INTO ai_revisits (draft_entry_id, decided, outcome, model_calls) VALUES ('D1', 1763000900, 'keep', 1)",
            transaction: tx);
        c.Execute("UPDATE meta SET value='3' WHERE key='schema_version'", transaction: tx);
        tx.Commit();
        c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }
}

using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

/// <summary>A frozen copy of the schema-4 DDL on top of <see cref="SchemaV3"/>, so later changes to EmailIndex can't change the migration fixture.</summary>
internal static class SchemaV4
{
    public const string Step4Ddl = """
        CREATE TABLE ai_calls (
          id INTEGER PRIMARY KEY,
          at INTEGER NOT NULL,
          kind TEXT NOT NULL,
          backend TEXT NOT NULL,
          outcome TEXT NOT NULL,
          input INTEGER, output INTEGER, cache_read INTEGER, cache_creation INTEGER);
        CREATE INDEX ix_ai_calls_at ON ai_calls(at);
        """;

    public static void Create(string dbPath)
    {
        SchemaV3.Create(dbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        c.Execute(Step4Ddl, transaction: tx);
        c.Execute("INSERT INTO ai_calls (at, kind, backend, outcome, input, output, cache_read, cache_creation) VALUES (1763000950, 'draft', 'cli', 'ok', 10, 20, NULL, NULL)",
            transaction: tx);
        c.Execute("UPDATE meta SET value='4' WHERE key='schema_version'", transaction: tx);
        tx.Commit();
        c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }
}

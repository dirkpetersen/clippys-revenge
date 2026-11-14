using SQLitePCL;

namespace ClippysRevenge.Core;

/// <summary>
/// Process-wide hard cap on SQLite's heap (sqlite3_hard_heap_limit64), a backstop for query_index (SPEC 11.12):
/// a few hundred bytes of nested WITH clauses or a few computed columns can otherwise make SQLite allocate gigabytes
/// while preparing or stepping a statement. When the cap is hit the statement fails with SQLITE_NOMEM and
/// query_index reports "needs too much memory". The cap is per process, not per connection, so it also covers the
/// sync writers; normal Clippy use (an index file of about 160 MB, small page caches) stays far below it, and a
/// NOMEM during sync just fails that sync cycle, which is retried.
/// </summary>
public static class SqliteHeapLimit
{
    public const long Bytes = 1024L * 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _done;

    /// <summary>Sets the cap once per process; later calls do nothing. Call before the first index is opened.</summary>
    public static void Ensure()
    {
        if (_done) return;
        lock (Gate)
        {
            if (_done) return;
            Batteries_V2.Init();   // idempotent; the provider must exist before the first raw call
            raw.sqlite3_hard_heap_limit64(Bytes);
            _done = true;
        }
    }
}
using Dapper;

namespace ClippysRevenge.Core;

// The ai_calls table (SPEC 9.1, 19.13). Times, kinds and token counts only: never text.
public sealed partial class EmailIndex
{
    /// <summary>One ai_calls row (SPEC 19.13). at as unix seconds UTC; kind lower-case enum name; outcome "ok"/"failed";
    /// null or negative token values are stored as NULL.</summary>
    /// <exception cref="ArgumentException">backend is not "cli" or "api" (ordinal).</exception>
    /// <exception cref="ArgumentOutOfRangeException">kind isn't defined.</exception>
    public void AddAiCall(DateTimeOffset at, ModelCallKind kind, string backend, bool ok, TokenUsage? usage)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!string.Equals(backend, "cli", StringComparison.Ordinal) && !string.Equals(backend, "api", StringComparison.Ordinal))
            throw new ArgumentException("backend must be \"cli\" or \"api\".", nameof(backend));

        static long? NonNeg(long? v) => v is >= 0 ? v : null;

        using var conn = OpenConnection();
        conn.Execute(
            "INSERT INTO ai_calls (at, kind, backend, outcome, input, output, cache_read, cache_creation) " +
            "VALUES (@at, @kind, @backend, @outcome, @input, @output, @cr, @cc)",
            new
            {
                at = at.ToUnixTimeSeconds(),
                kind = kind.ToString().ToLowerInvariant(),
                backend,
                outcome = ok ? "ok" : "failed",
                input = NonNeg(usage?.Input),
                output = NonNeg(usage?.Output),
                cr = NonNeg(usage?.CacheRead),
                cc = NonNeg(usage?.CacheCreation),
            });
    }

    /// <summary>Totals over rows with at &gt;= since (unix seconds). No rows gives AiUsageTotals.Zero.</summary>
    public AiUsageTotals AiUsageSince(DateTimeOffset since)
    {
        using var conn = OpenConnection();
        var r = conn.QuerySingle<UsageRow>(
            """
            SELECT COUNT(*) AS Calls,
                   COALESCE(SUM(CASE WHEN input IS NULL AND output IS NULL THEN 1 ELSE 0 END), 0) AS Unknown,
                   COALESCE(SUM(input), 0) AS Input, COALESCE(SUM(output), 0) AS Output,
                   COALESCE(SUM(cache_read), 0) AS CacheRead, COALESCE(SUM(cache_creation), 0) AS CacheCreation
            FROM ai_calls WHERE at >= @s
            """,
            new { s = since.ToUnixTimeSeconds() });
        return new AiUsageTotals((int)r.Calls, (int)r.Unknown, r.Input, r.Output, r.CacheRead, r.CacheCreation);
    }

    private sealed class UsageRow
    {
        public long Calls { get; set; }
        public long Unknown { get; set; }
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation { get; set; }
    }
}

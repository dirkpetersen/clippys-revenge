using System.Diagnostics;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Runs only when CLIPPY_PERF=1 (SPEC §17).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("CLIPPY_PERF") != "1")
            Skip = "Set CLIPPY_PERF=1 to run the performance benchmark.";
    }
}

public class PerfBenchmarkTests : IDisposable
{
    private const int PerMailbox = 50_000;
    private const int Runs = 200;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Zipf-like: a few common words, a long tail, so query terms have realistic selectivity.
    private static string Vocab(Random r) => "w" + (int)(5000 * Math.Pow(r.NextDouble(), 3));

    private static void Fill(EmailIndex ix, int seed)
    {
        var rnd = new Random(seed);
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var batch = new List<IndexedEmail>(1000);
        for (var i = 0; i < PerMailbox; i++)
        {
            string W() => Vocab(rnd);
            var body = string.Join(' ', Enumerable.Range(0, 60).Select(_ => W()));
            batch.Add(new IndexedEmail
            {
                EntryId = $"{seed}-{i}",
                Folder = i % 5 == 0 ? "Sent Items" : "Inbox",
                Subject = $"{W()} {W()} {i}",
                SenderName = $"Person {rnd.Next(500)}",
                SenderEmail = $"p{rnd.Next(500)}@x.edu",
                To = $"q{rnd.Next(500)}@x.edu",
                EventTime = start.AddMinutes(i * 10),
                Unread = i % 7 == 0,
                Body = body,
            });
            if (batch.Count == 1000) { ix.UpsertBatch(batch); batch.Clear(); }
        }
        if (batch.Count > 0) ix.UpsertBatch(batch);
    }

    private static double P95(Action op)
    {
        for (var i = 0; i < 10; i++) op();
        var times = new double[Runs];
        for (var i = 0; i < Runs; i++)
        {
            var t = Stopwatch.GetTimestamp();
            op();
            times[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Array.Sort(times);
        return times[(int)Math.Ceiling(Runs * 0.95) - 1];
    }

    [PerfFact]
    public void Search_P95_MeetsTargets()
    {
        using var mi = new MailboxIndexes(_dir, ["a@x.edu", "b@x.edu"]);
        Fill(mi.For(""), 1);
        Fill(mi.For("a@x.edu"), 2);
        Fill(mi.For("b@x.edu"), 3);

        var rnd = new Random(42);
        SearchFilter Next() => rnd.Next(4) switch
        {
            0 => new SearchFilter { Query = "w" + rnd.Next(100, 3000) },
            1 => new SearchFilter { Query = "w" + rnd.Next(100, 3000) + " w" + rnd.Next(100, 3000) },
            2 => new SearchFilter { Sender = "Person " + rnd.Next(500), Folder = "Inbox" },
            _ => new SearchFilter { Unread = true, DateFrom = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero) },
        };

        for (var k = 0; k < 4; k++)
        {
            var kk = k;
            var r2 = new Random(7);
            var v = P95(() => mi.Search(kk switch
            {
                0 => new SearchFilter { Query = "w" + r2.Next(100, 3000) },
                1 => new SearchFilter { Query = "w" + r2.Next(100, 3000) + " w" + r2.Next(100, 3000) },
                2 => new SearchFilter { Sender = "Person " + r2.Next(500), Folder = "Inbox" },
                _ => new SearchFilter { Unread = true, DateFrom = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero) },
            }, ""));
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "clippy-perf-detail.txt"), $"type{kk}={v:F2}\n");
        }
        var single = P95(() => mi.Search(Next(), ""));
        var all = P95(() => mi.Search(Next(), MailboxIndexes.AllMailboxes));
        Console.Error.WriteLine($"PERF single p95 = {single:F2} ms (target < 20); all x3 p95 = {all:F2} ms (target < 50); {PerMailbox} emails per mailbox");

        Assert.True(single < 20, $"single p95 {single:F2} ms");
        Assert.True(all < 50, $"all p95 {all:F2} ms");
    }
}

using Xunit;

namespace ClippysRevenge.Core.Tests;

public class FreeBusyFetchTests
{
    private static readonly DateTime D0 = new(2026, 10, 12);

    [Fact]
    public void Single_chunk()
    {
        var calls = new List<DateTime>();
        var r = FreeBusy.FetchChunked(D0.AddHours(9), D0.AddHours(17), 30, s => { calls.Add(s); return "0202"; });
        Assert.Equal("0202", r);
        Assert.Equal([D0], calls);
    }

    private static string? Fetch(DateTime from, DateTime to, List<DateTime> calls, Func<int, string?> make) =>
        FreeBusy.FetchChunked(from, to, 30, s => { calls.Add(s); return make(calls.Count - 1); });

    [Fact]
    public void Three_chunks_joined()
    {
        var calls = new List<DateTime>();
        var r = Fetch(D0, D0.AddDays(60), calls, i => new string("023"[i], 1440));
        Assert.Equal([D0, D0.AddDays(28), D0.AddDays(56)], calls);
        Assert.Equal(1344 + 1344 + 1440, r!.Length);
        Assert.Equal('0', r[0]);
        Assert.Equal('2', r[1344]);
        Assert.Equal('3', r[2688]);
    }

    [Fact]
    public void Null_chunk_stops()
    {
        var calls = new List<DateTime>();
        Assert.Null(Fetch(D0, D0.AddDays(60), calls, i => i == 1 ? null : new string('0', 1440)));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Short_chunk_is_null()
    {
        var calls = new List<DateTime>();
        Assert.Null(Fetch(D0, D0.AddDays(60), calls, i => new string('0', i == 0 ? 1343 : 1440)));
    }

    [Fact]
    public void Empty_range()
    {
        var calls = new List<DateTime>();
        Assert.Equal("", Fetch(D0, D0, calls, _ => "x"));
        Assert.Equal("", Fetch(D0, D0.AddDays(-1), calls, _ => "x"));
        Assert.Empty(calls);
    }

    [Fact]
    public void Invalid_arguments()
    {
        var calls = 0;
        Func<DateTime, string?> f = _ => { calls++; return ""; };
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeBusy.FetchChunked(D0, D0.AddDays(1), 0, f));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeBusy.FetchChunked(D0, D0.AddDays(1), 7, f));
        Assert.Throws<ArgumentNullException>(() => FreeBusy.FetchChunked(D0, D0.AddDays(1), 30, null!));
        Assert.Equal(0, calls);
    }
}

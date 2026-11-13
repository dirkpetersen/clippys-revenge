using Xunit;

namespace ClippysRevenge.Core.Tests;

public class FreeBusyTests
{
    static DateTime T(int day, int h, int m = 0) => new(2026, 10, day, h, m, 0);
    static TimeRange R(DateTime a, DateTime b) => new(a, b);

    static readonly DateTime Now0 = T(11, 12);

    static IReadOnlyList<TimeRange> Slots(
        IEnumerable<IReadOnlyList<TimeRange>> busy, DateTime from, DateTime to,
        int sh = 8, int eh = 17, bool biz = true, int min = 60, DateTime? now = null, int interval = 30)
        => FreeBusy.FreeSlots(busy, from, to, sh, eh, biz, min, now ?? Now0, interval);

    static IReadOnlyList<TimeRange>[] One(params TimeRange[] r) => new IReadOnlyList<TimeRange>[] { r };

    // BusyIntervals
    [Fact]
    public void Busy_Empty() => Assert.Empty(FreeBusy.BusyIntervals("", T(12, 0), 30, true));

    [Fact]
    public void Busy_AllFree() => Assert.Empty(FreeBusy.BusyIntervals("0000", T(12, 0), 30, true));

    [Fact]
    public void Busy_Run() =>
        Assert.Equal(new[] { R(T(12, 0, 30), T(12, 1, 30)) }, FreeBusy.BusyIntervals("0220", T(12, 0), 30, true));

    [Fact]
    public void Busy_MergesBusyAndOof() =>
        Assert.Equal(new[] { R(T(12, 0), T(12, 1)), R(T(12, 1, 30), T(12, 2)) },
            FreeBusy.BusyIntervals("2302", T(12, 0), 30, true));

    [Fact]
    public void Busy_Tentative()
    {
        Assert.Equal(new[] { R(T(12, 0, 30), T(12, 1, 30)) }, FreeBusy.BusyIntervals("0120", T(12, 0), 30, true));
        Assert.Equal(new[] { R(T(12, 1), T(12, 1, 30)) }, FreeBusy.BusyIntervals("0120", T(12, 0), 30, false));
    }

    [Fact]
    public void Busy_WorkingElsewhereIsFree() => Assert.Empty(FreeBusy.BusyIntervals("040", T(12, 0), 30, true));

    [Fact]
    public void Busy_RunAtEndClosed() =>
        Assert.Equal(new[] { R(T(12, 2), T(12, 3)) }, FreeBusy.BusyIntervals("002", T(12, 0), 60, true));

    [Fact]
    public void Busy_StartNotTruncated() =>
        Assert.Equal(new[] { R(T(12, 9), T(12, 9, 15)) }, FreeBusy.BusyIntervals("2", T(12, 9), 15, true));

    [Fact]
    public void Busy_NoInformationIsFree() => Assert.Empty(FreeBusy.BusyIntervals("055", T(12, 0), 30, true));

    [Fact]
    public void Busy_NoInformationEndsRun() =>
        Assert.Equal(new[] { R(T(12, 0), T(12, 0, 30)) }, FreeBusy.BusyIntervals("255", T(12, 0), 30, true));

    [Fact]
    public void Busy_BadChar()
    {
        var ex = Assert.Throws<FormatException>(() => FreeBusy.BusyIntervals("02x", T(12, 0), 30, true));
        Assert.Equal("Unexpected free/busy code 'x' at position 2.", ex.Message);
    }

    [Fact]
    public void Busy_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeBusy.BusyIntervals("0", T(12, 0), 0, true));
        Assert.Throws<ArgumentNullException>(() => FreeBusy.BusyIntervals(null!, T(12, 0), 30, true));
    }

    // FreeSlots
    [Fact]
    public void Free_NoAttendees() =>
        Assert.Equal(new[] { R(T(12, 8), T(12, 17)) },
            Slots(Array.Empty<IReadOnlyList<TimeRange>>(), T(12, 0), T(13, 0)));

    [Fact]
    public void Free_OverlapsAcrossAttendees()
    {
        var a = new[] { R(T(12, 9), T(12, 10)), R(T(12, 12), T(12, 13)) };
        var b = new[] { R(T(12, 9, 30), T(12, 11)), R(T(12, 12, 30), T(12, 14)) };
        var r = Slots(new IReadOnlyList<TimeRange>[] { a, b }, T(12, 0), T(13, 0));
        Assert.Equal(new[] { R(T(12, 8), T(12, 9)), R(T(12, 11), T(12, 12)), R(T(12, 14), T(12, 17)) }, r);
    }

    [Fact]
    public void Free_UnsortedInput() =>
        Assert.Equal(new[] { R(T(12, 8), T(12, 9)), R(T(12, 10), T(12, 14)), R(T(12, 15), T(12, 17)) },
            Slots(One(R(T(12, 14), T(12, 15)), R(T(12, 9), T(12, 10))), T(12, 0), T(13, 0)));

    [Fact]
    public void Free_MinMinutes()
    {
        var busy = One(R(T(12, 8, 30), T(12, 17)));
        Assert.Empty(Slots(busy, T(12, 0), T(13, 0), min: 60));
        Assert.Equal(new[] { R(T(12, 8), T(12, 8, 30)) }, Slots(busy, T(12, 0), T(13, 0), min: 30));
    }

    [Fact]
    public void Free_Weekends()
    {
        var none = Array.Empty<IReadOnlyList<TimeRange>>();
        Assert.Equal(new[] { R(T(16, 8), T(16, 17)), R(T(19, 8), T(19, 17)) }, Slots(none, T(16, 0), T(20, 0)));
        Assert.Equal(4, Slots(none, T(16, 0), T(20, 0), biz: false).Count);
    }

    [Fact]
    public void Free_WorkHourEdges()
    {
        Assert.Equal(new[] { R(T(12, 8), T(12, 17)) },
            Slots(One(R(T(12, 7), T(12, 8)), R(T(12, 17), T(12, 18))), T(12, 0), T(13, 0)));
        Assert.Equal(new[] { R(T(12, 8, 30), T(12, 17)) },
            Slots(One(R(T(12, 7, 30), T(12, 8, 30))), T(12, 0), T(13, 0)));
    }

    [Fact]
    public void Free_BusyAcrossMidnight() =>
        Assert.Equal(new[] { R(T(12, 8), T(12, 16)), R(T(13, 9), T(13, 17)) },
            Slots(One(R(T(12, 16), T(13, 9))), T(12, 0), T(14, 0)));

    [Fact]
    public void Free_NowCutoff()
    {
        var none = Array.Empty<IReadOnlyList<TimeRange>>();
        Assert.Equal(new[] { R(T(12, 10, 30), T(12, 17)) }, Slots(none, T(12, 0), T(13, 0), now: T(12, 10, 7)));
        Assert.Equal(new[] { R(T(12, 10, 30), T(12, 17)) }, Slots(none, T(12, 0), T(13, 0), now: T(12, 10, 30)));
        Assert.Empty(Slots(none, T(12, 0), T(13, 0), now: T(12, 16, 31)));
        Assert.Equal(new[] { R(T(12, 16, 30), T(12, 17)) },
            Slots(none, T(12, 0), T(13, 0), min: 30, now: T(12, 16, 1)));
        Assert.Empty(Slots(none, T(12, 0), T(13, 0), min: 60, now: T(12, 16, 1)));
        Assert.Equal(T(12, 10, 15), Slots(none, T(12, 0), T(13, 0), now: T(12, 10, 7), interval: 15)[0].Start);
        Assert.Empty(Slots(none, T(12, 0), T(13, 0), now: T(14, 0)));
        Assert.Equal(new[] { R(T(13, 8), T(13, 17)) }, Slots(none, T(12, 0), T(14, 0), now: T(12, 23, 50)));
    }

    [Fact]
    public void Free_PartialDay() =>
        Assert.Equal(new[] { R(T(12, 13), T(12, 15)) },
            Slots(Array.Empty<IReadOnlyList<TimeRange>>(), T(12, 13), T(12, 15)));

    [Fact]
    public void Free_FromNotBeforeTo()
    {
        var none = Array.Empty<IReadOnlyList<TimeRange>>();
        Assert.Empty(Slots(none, T(12, 0), T(12, 0)));
        Assert.Empty(Slots(none, T(13, 0), T(12, 0)));
    }

    [Fact]
    public void Free_FullDayWindow() =>
        Assert.Equal(new[] { R(T(17, 0), T(18, 0)) },
            Slots(Array.Empty<IReadOnlyList<TimeRange>>(), T(17, 0), T(18, 0), sh: 0, eh: 24, biz: false));

    [Fact]
    public void Free_Validation()
    {
        var none = Array.Empty<IReadOnlyList<TimeRange>>();
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), sh: 17, eh: 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), sh: 8, eh: 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), sh: -1, eh: 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), sh: 8, eh: 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), min: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slots(none, T(12, 0), T(13, 0), interval: 0));
        Assert.Throws<ArgumentNullException>(() => Slots(null!, T(12, 0), T(13, 0)));
        Assert.Throws<ArgumentNullException>(() =>
            Slots(new IReadOnlyList<TimeRange>[] { null! }, T(12, 0), T(13, 0)));
    }

    static string Mon(char c, int from, int to)
    {
        var a = new string('0', 48).ToCharArray();
        for (var i = from; i <= to; i++) a[i] = c;
        return new string(a);
    }

    [Fact]
    public void Free_CombinedWithBusyIntervals()
    {
        foreach (var (tent, expected) in new[]
        {
            (true, new[] { R(T(12, 8), T(12, 9)), R(T(12, 11), T(12, 17)) }),
            (false, new[] { R(T(12, 8), T(12, 9)), R(T(12, 10), T(12, 17)) }),
        })
        {
            var a = FreeBusy.BusyIntervals(Mon('2', 18, 19), T(12, 0), 30, tent);
            var b = FreeBusy.BusyIntervals(Mon('1', 20, 21), T(12, 0), 30, tent);
            Assert.Equal(expected, Slots(new[] { a, b }, T(12, 0), T(13, 0)));
        }
    }

    // Chunks
    [Fact]
    public void Chunk_Single() =>
        Assert.Equal(new[] { T(12, 0) }, FreeBusy.ChunkStarts(T(12, 14), T(22, 0)));

    [Fact]
    public void Chunk_Sixty() =>
        Assert.Equal(new[] { new DateTime(2026, 10, 12), new DateTime(2026, 11, 9), new DateTime(2026, 12, 7) },
            FreeBusy.ChunkStarts(T(12, 0), T(12, 0).AddDays(60)));

    [Fact]
    public void Chunk_Boundary()
    {
        Assert.Single(FreeBusy.ChunkStarts(T(12, 0), T(12, 0).AddDays(28)));
        Assert.Equal(2, FreeBusy.ChunkStarts(T(12, 0), T(12, 0).AddDays(28).AddMinutes(1)).Count);
    }

    [Fact]
    public void Chunk_Empty()
    {
        Assert.Empty(FreeBusy.ChunkStarts(T(12, 0), T(12, 0)));
        Assert.Empty(FreeBusy.ChunkStarts(T(12, 0), T(11, 0)));
    }

    [Fact]
    public void Join_EmptyAndSingle()
    {
        Assert.Equal("", FreeBusy.JoinChunks(Array.Empty<string>(), 60));
        Assert.Equal("0120", FreeBusy.JoinChunks(new[] { "0120" }, 60));
    }

    static string Joined()
    {
        var c0 = new string('0', 720).ToCharArray();
        c0[671] = '2';
        c0[700] = '3';
        var c1 = "2" + new string('0', 99);
        return FreeBusy.JoinChunks(new[] { new string(c0), c1 }, 60);
    }

    [Fact]
    public void Join_TrimsNonLast()
    {
        var j = Joined();
        Assert.Equal(772, j.Length);
        Assert.Equal('2', j[671]);
        Assert.Equal('2', j[672]);
        Assert.DoesNotContain('3', j);
    }

    [Fact]
    public void Join_BoundaryMerge() =>
        Assert.Equal(new[] { R(T(12, 0).AddHours(671), T(12, 0).AddHours(673)) },
            FreeBusy.BusyIntervals(Joined(), T(12, 0), 60, true));

    [Fact]
    public void Join_ThreeChunks()
    {
        var chunks = new[] { new string('0', 1440), new string('1', 1440), new string('2', 1440) };
        var j = FreeBusy.JoinChunks(chunks, 30);
        Assert.Equal(1344 + 1344 + 1440, j.Length);
        Assert.Equal(new string('0', 1344) + new string('1', 1344) + new string('2', 1440), j);
    }

    [Fact]
    public void Join_ShortNonLast()
    {
        var ex = Assert.Throws<ArgumentException>(() => FreeBusy.JoinChunks(new[] { new string('0', 100), "0" }, 60));
        Assert.StartsWith("Free/busy chunk 0 has 100 slots; expected at least 672.", ex.Message);
        Assert.Equal(672 + 1, FreeBusy.JoinChunks(new[] { new string('0', 672), "0" }, 60).Length);
    }

    [Fact]
    public void Join_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeBusy.JoinChunks(new[] { "0" }, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeBusy.JoinChunks(new[] { "0" }, 0));
        Assert.Throws<ArgumentNullException>(() => FreeBusy.JoinChunks(null!, 60));
        Assert.Throws<ArgumentNullException>(() => FreeBusy.JoinChunks(new string[] { null! }, 60));
    }
}

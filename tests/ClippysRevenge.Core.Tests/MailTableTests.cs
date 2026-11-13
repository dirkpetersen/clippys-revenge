using System.Globalization;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class MailTableTests
{
    private static DateTimeOffset U(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUniversalTime();

    private static DateTime Utc(string iso) =>
        DateTime.SpecifyKind(DateTime.Parse(iso, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    private static object?[] Raw(string? id, DateTime? lm, DateTime? evt, object? unread = null, string? imid = null,
        string? cls = "IPM.Note") =>
        [id, (object?)lm ?? DBNull.Value, (object?)evt ?? DBNull.Value, unread, imid, cls];

    private static readonly DateTime Base = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static MailTableRow Row(int minute) =>
        new("id" + minute, new DateTimeOffset(Base.AddMinutes(minute), TimeSpan.Zero),
            new DateTimeOffset(Base.AddMinutes(minute), TimeSpan.Zero), false, null);

    private static List<MailTableRow> Rows(params int[] minutes) => minutes.Select(Row).ToList();

    private static int[] Minutes(MailTablePage p) =>
        p.Rows.Select(r => (int)(r.LastModified - new DateTimeOffset(Base, TimeSpan.Zero)).TotalMinutes).ToArray();

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    [InlineData("en-US")]
    public void Literal_is_culture_invariant(string culture)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("2026-03-04 05:06", DaslFilter.Literal(U("2026-03-04T05:06:59.9Z")));
            Assert.Equal("2026-03-04 09:00", DaslFilter.Literal(U("2026-03-04T01:00:00-08:00")));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void Changed_exact_string() =>
        Assert.Equal(
            "@SQL=(\"http://schemas.microsoft.com/mapi/proptag/0x30080040\" >= '2026-03-09 12:00') AND (\"http://schemas.microsoft.com/mapi/proptag/0x0E060040\" >= '2025-03-09 00:00') AND (\"http://schemas.microsoft.com/mapi/proptag/0x001A001F\" LIKE 'IPM.Note%')",
            DaslFilter.Changed(EventTimeField.Received, U("2026-03-10T12:00:30Z"), U("2025-03-10T00:00:00Z")));

    [Fact]
    public void Range_exact_string() =>
        Assert.Equal(
            "@SQL=(\"http://schemas.microsoft.com/mapi/proptag/0x00390040\" >= '2024-12-31 00:00') AND (\"http://schemas.microsoft.com/mapi/proptag/0x00390040\" < '2025-02-02 10:30') AND (\"http://schemas.microsoft.com/mapi/proptag/0x001A001F\" LIKE 'IPM.Note%')",
            DaslFilter.Range(EventTimeField.Sent, U("2025-01-01T00:00:00Z"), U("2025-02-01T10:30:15Z")));

    [Fact]
    public void Since_exact_string() =>
        Assert.Equal(
            "@SQL=(\"http://schemas.microsoft.com/mapi/proptag/0x0E060040\" >= '2025-12-31 00:00') AND (\"http://schemas.microsoft.com/mapi/proptag/0x001A001F\" LIKE 'IPM.Note%')",
            DaslFilter.Since(EventTimeField.Received, U("2026-01-01T00:00:00Z")));

    [Fact]
    public void ByMessageId_quotes_and_validates()
    {
        Assert.Equal("@SQL=\"http://schemas.microsoft.com/mapi/proptag/0x1035001F\" = '<abc@x.org>'",
            DaslFilter.ByMessageId("<abc@x.org>"));
        Assert.Equal("@SQL=\"http://schemas.microsoft.com/mapi/proptag/0x1035001F\" = '<o''example@x.org>'",
            DaslFilter.ByMessageId("<o'example@x.org>"));
        Assert.ThrowsAny<ArgumentException>(() => DaslFilter.ByMessageId(null!));
        Assert.Throws<ArgumentException>(() => DaslFilter.ByMessageId(""));
        Assert.Throws<ArgumentException>(() => DaslFilter.ByMessageId("  "));
    }

    [Fact]
    public void ParseEntryId_prefers_long_term_and_falls_back()
    {
        Assert.Equal("00AB01FF", MailTable.ParseEntryId(new byte[] { 0x00, 0xAB, 0x01, 0xFF }, "EF00"));
        Assert.Equal("00AB01FF", MailTable.ParseEntryId("00ab01ff", "EF00"));
        Assert.Equal("EF00", MailTable.ParseEntryId(null, " EF00 "));
        Assert.Equal("EF00", MailTable.ParseEntryId(DBNull.Value, "EF00"));
        Assert.Equal("EF00", MailTable.ParseEntryId(Array.Empty<byte>(), "EF00"));
        Assert.Equal("EF00", MailTable.ParseEntryId("  ", "EF00"));
        Assert.Null(MailTable.ParseEntryId(null, null));
        Assert.Null(MailTable.ParseEntryId(Array.Empty<byte>(), ""));
    }

    [Fact]
    public void ToRow_uses_long_term_id_when_present()
    {
        var evt = new DateTime(2026, 3, 1, 8, 0, 0);
        var raw = Raw("EF00", evt, evt).Append(new byte[] { 0x0A, 0xBC }).ToArray();
        Assert.Equal("0ABC", MailTable.ToRow(raw)!.EntryId);
        Assert.Equal("EF00", MailTable.ToRow(Raw("EF00", evt, evt))!.EntryId);
    }

    [Fact]
    public void Columns_and_sorts()
    {
        Assert.Equal(new[]
        {
            "EntryID", "http://schemas.microsoft.com/mapi/proptag/0x30080040", "http://schemas.microsoft.com/mapi/proptag/0x0E060040",
            "UnRead", "http://schemas.microsoft.com/mapi/proptag/0x1035001F", "http://schemas.microsoft.com/mapi/proptag/0x001A001F",
            "http://schemas.microsoft.com/mapi/proptag/0x66700102",
        }, MailTable.Columns(EventTimeField.Received));
        Assert.Equal("http://schemas.microsoft.com/mapi/proptag/0x00390040", MailTable.Columns(EventTimeField.Sent)[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MailTable.EventTimeProp((EventTimeField)9));
        Assert.Equal("[ReceivedTime]", MailTable.EventTimeSort(EventTimeField.Received));
        Assert.Equal("[SentOn]", MailTable.EventTimeSort(EventTimeField.Sent));
    }

    [Theory]
    [InlineData("IPM.Note", true)]
    [InlineData("ipm.note", true)]
    [InlineData("IPM.Note.SMIME", true)]
    [InlineData("IPM.Note.Microsoft.Voicemail", true)]
    [InlineData("IPM.NoteX", false)]
    [InlineData("IPM.Schedule.Meeting.Request", false)]
    [InlineData("REPORT.IPM.Note.NDR", false)]
    [InlineData("IPM.Appointment", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsIndexedClass(string? cls, bool expected) => Assert.Equal(expected, MailTable.IsIndexedClass(cls));

    [Fact]
    public void ToRow_happy_path()
    {
        var row = MailTable.ToRow(Raw("00AB", new DateTime(2026, 3, 4, 5, 6, 7, 500), new DateTime(2026, 3, 1, 8, 0, 0), true, " <m@x> "))!;
        Assert.Equal("00AB", row.EntryId);
        Assert.Equal(U("2026-03-04T05:06:07Z"), row.LastModified);
        Assert.Equal(U("2026-03-01T08:00:00Z"), row.EventTime);
        Assert.True(row.Unread);
        Assert.Equal("<m@x>", row.InternetMessageId);
        Assert.Equal(TimeSpan.Zero, row.LastModified.Offset);
        Assert.Equal(TimeSpan.Zero, row.EventTime.Offset);
    }

    [Fact]
    public void ToRow_nulls_and_defaults()
    {
        var evt = new DateTime(2026, 3, 1, 8, 0, 0);
        Assert.Null(MailTable.ToRow(Raw(null, evt, evt)));
        Assert.Null(MailTable.ToRow(Raw("", evt, evt)));
        var dbnull = Raw("a", evt, evt);
        dbnull[0] = DBNull.Value;
        Assert.Null(MailTable.ToRow(dbnull));
        Assert.Null(MailTable.ToRow(Raw("a", evt, null)));
        Assert.Null(MailTable.ToRow(Raw("a", evt, new DateTime(4501, 1, 1))));
        Assert.Null(MailTable.ToRow(Raw("a", evt, DateTime.MinValue)));
        Assert.Null(MailTable.ToRow(Raw("a", evt, evt, cls: "IPM.Schedule.Meeting.Request")));
        var noLm = MailTable.ToRow(Raw("a", null, evt))!;
        Assert.Equal(noLm.EventTime, noLm.LastModified);
        Assert.False(MailTable.ToRow(Raw("a", evt, evt, DBNull.Value))!.Unread);
        Assert.False(MailTable.ToRow(Raw("a", evt, evt, false))!.Unread);
        Assert.False(MailTable.ToRow(Raw("a", evt, evt, 1))!.Unread);
        Assert.Null(MailTable.ToRow(Raw("a", evt, evt, imid: ""))!.InternetMessageId);
        Assert.Throws<ArgumentException>(() => MailTable.ToRow(new object?[5]));
        Assert.Throws<ArgumentNullException>(() => MailTable.ToRow(null!));
    }

    [Fact]
    public void FromTableValue_cases()
    {
        var local = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(new DateTimeOffset(local.ToUniversalTime()), OutlookDates.FromTableValue(local));
        Assert.Null(OutlookDates.FromTableValue("2026-01-01"));
        Assert.Null(OutlookDates.FromTableValue(null));
        Assert.Null(OutlookDates.FromTableValue(DBNull.Value));
        Assert.Null(OutlookDates.FromTableValue(new DateTime(4501, 1, 1)));
        Assert.Null(OutlookDates.FromTableValue(new DateTime(1601, 1, 1)));
        Assert.Equal(U("1601-01-02T00:00:00Z"), OutlookDates.FromTableValue(new DateTime(1601, 1, 2)));
    }

    [Fact]
    public void FromLocal_cases()
    {
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        Assert.Equal(U("2026-07-01T17:00:00Z"), OutlookDates.FromLocal(new DateTime(2026, 7, 1, 10, 0, 0), la));
        Assert.Equal(U("2026-01-15T18:00:00Z"), OutlookDates.FromLocal(new DateTime(2026, 1, 15, 10, 0, 0, 900), la));
        foreach (var local in new[] { new DateTime(2026, 3, 8, 2, 30, 0), new DateTime(2026, 11, 1, 1, 30, 0) })
        {
            var expected = OutlookDates.TruncateToSecond(new DateTimeOffset(local - la.GetUtcOffset(local), TimeSpan.Zero));
            Assert.Equal(expected, OutlookDates.FromLocal(local, la));
        }
        Assert.Null(OutlookDates.FromLocal(new DateTime(4501, 1, 1), la));
        Assert.Throws<ArgumentNullException>(() => OutlookDates.FromLocal(DateTime.Now, null!));
    }

    [Fact]
    public void Pager_basics()
    {
        Assert.Equal([1, 2], Minutes(Page(2, 1, 2, 3, 4, 5)));
        Assert.True(Page(2, 1, 2, 3, 4, 5).HasMore);
        var tie = Page(2, 1, 2, 2, 2, 3);
        Assert.Equal([1, 2, 2, 2], Minutes(tie));
        Assert.True(tie.HasMore);
        var tail = Page(2, 1, 2, 2);
        Assert.Equal([1, 2, 2], Minutes(tail));
        Assert.False(tail.HasMore);
        var same = Page(2, 5, 5, 5, 5);
        Assert.Equal(4, same.Rows.Count);
        Assert.False(same.HasMore);
        var empty = Page(2);
        Assert.Empty(empty.Rows);
        Assert.False(empty.HasMore);
    }

    private static MailTablePage Page(int max, params int[] minutes) =>
        MailTablePager.Take(Rows(minutes), _ => true, r => r.LastModified, false, max);

    [Fact]
    public void Pager_keep()
    {
        var p = MailTablePager.Take(Rows(1, 3, 4), r => r.EntryId != "id3", r => r.LastModified, false, 2);
        Assert.Equal([1, 4], Minutes(p));
        Assert.False(p.HasMore);
        var q = MailTablePager.Take(Rows(1, 2, 9), r => r.EntryId != "id9", r => r.LastModified, false, 2);
        Assert.Equal([1, 2], Minutes(q));
        Assert.False(q.HasMore);
    }

    [Fact]
    public void Pager_descending()
    {
        var p = MailTablePager.Take(Rows(5, 4, 4, 3), _ => true, r => r.LastModified, true, 2);
        Assert.Equal([5, 4, 4], Minutes(p));
        Assert.True(p.HasMore);
    }

    private sealed class Counting(int n) : IEnumerable<MailTableRow>
    {
        public int MoveNexts;
        public bool Disposed;

        public IEnumerator<MailTableRow> GetEnumerator() => Iterate();

        private IEnumerator<MailTableRow> Iterate()
        {
            try
            {
                for (var i = 0; i < n; i++)
                {
                    MoveNexts++;
                    yield return Row(i);
                }
            }
            finally
            {
                Disposed = true;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Pager_is_lazy_and_disposes()
    {
        var src = new Counting(1000);
        var p = MailTablePager.Take(src, _ => true, r => r.LastModified, false, 10);
        Assert.Equal(10, p.Rows.Count);
        Assert.Equal(11, src.MoveNexts);
        Assert.True(src.Disposed);
    }

    [Fact]
    public void Pager_order_check()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MailTablePager.Take(Rows(1, 3, 2), _ => true, r => r.LastModified, false, 5));
        Assert.Equal("Table rows are not sorted.", ex.Message);
        Assert.Throws<InvalidOperationException>(() => MailTablePager.Take(Rows(1, 3, 2), r => r.EntryId != "id2", r => r.LastModified, false, 5));
        Assert.Throws<InvalidOperationException>(() => MailTablePager.Take(Rows(3, 1, 2), _ => true, r => r.LastModified, true, 5));
    }

    [Fact]
    public void Pager_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(5001, 1));
        Assert.Throws<ArgumentNullException>(() => MailTablePager.Take(null!, _ => true, r => r.LastModified, false, 1));
        Assert.Throws<ArgumentNullException>(() => MailTablePager.Take([], null!, r => r.LastModified, false, 1));
        Assert.Throws<ArgumentNullException>(() => MailTablePager.Take([], _ => true, null!, false, 1));
    }

    private static readonly DateTime T0 = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Changed_filters_exactly()
    {
        var evt = T0.AddDays(-1);
        var raw = new[]
        {
            Raw("a", T0.AddSeconds(-1), evt),
            Raw("b", T0, evt),
            Raw("c", T0.AddSeconds(1), evt),
        };
        var page = MailTableQueries.Changed(raw, new DateTimeOffset(T0.AddMilliseconds(700), TimeSpan.Zero), U("2026-03-01T00:00:00Z"));
        Assert.Equal(["b", "c"], page.Rows.Select(r => r.EntryId));

        var old = new[] { Raw("a", T0, U("2026-02-01T00:00:00Z").UtcDateTime) };
        Assert.Empty(MailTableQueries.Changed(old, U("2026-03-10T00:00:00Z"), U("2026-03-01T00:00:00Z")).Rows);

        var meeting = new[] { Raw("a", T0, evt, cls: "IPM.Schedule.Meeting.Request") };
        Assert.Empty(MailTableQueries.Changed(meeting, U("2026-03-10T00:00:00Z"), U("2026-03-01T00:00:00Z")).Rows);

        var unsorted = new[] { Raw("a", T0.AddSeconds(5), evt), Raw("b", T0, evt) };
        Assert.Throws<InvalidOperationException>(() =>
            MailTableQueries.Changed(unsorted, U("2026-03-10T00:00:00Z"), U("2026-03-01T00:00:00Z")));
    }

    [Fact]
    public void Range_filters_exactly()
    {
        var from = U("2026-03-01T00:00:00Z");
        var to = U("2026-03-10T00:00:00Z");
        var lm = T0;
        var raw = new[]
        {
            Raw("to", lm, to.UtcDateTime),
            Raw("toM1", lm, to.UtcDateTime.AddSeconds(-1)),
            Raw("from", lm, from.UtcDateTime),
            Raw("fromM1", lm, from.UtcDateTime.AddSeconds(-1)),
        };
        var page = MailTableQueries.Range(raw, from, to);
        Assert.Equal(["toM1", "from"], page.Rows.Select(r => r.EntryId));

        IEnumerable<IReadOnlyList<object?>> Boom()
        {
            throw new InvalidOperationException("enumerated");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        Assert.Empty(MailTableQueries.Range(Boom(), to, to).Rows);
        var gt = MailTableQueries.Range(Boom(), to, from);
        Assert.Empty(gt.Rows);
        Assert.False(gt.HasMore);
    }

    [Fact]
    public void EntryIds_dedupe_and_filter()
    {
        var since = U("2026-03-05T00:00:00Z");
        var ok = since.UtcDateTime;
        var raw = new[]
        {
            Raw("b", T0, ok, true),
            Raw("a", T0, ok.AddDays(1), false),
            Raw("b", T0, ok.AddDays(2), false),
            Raw("old", T0, ok.AddSeconds(-1)),
            Raw("mtg", T0, ok, cls: "IPM.Schedule.Meeting.Request"),
            Raw("nodate", T0, null),
        };
        var ids = MailTableQueries.EntryIds(raw, since);
        Assert.Equal([new MailEntryState("b", true), new MailEntryState("a", false)], ids);
    }

    [Fact]
    public void Tie_safe_recipe_is_lossless()
    {
        var keys = new[] { 1, 2, 2, 2, 3, 4, 4 };
        var rawAsc = keys.Select((k, i) => Raw("e" + i, Base.AddMinutes(k), Base.AddMinutes(k))).ToList();
        var seen = new List<string>();
        var from = new DateTimeOffset(Base, TimeSpan.Zero);
        var cutoff = from;
        while (true)
        {
            var page = MailTableQueries.Changed(rawAsc, from, cutoff, 2);
            seen.AddRange(page.Rows.Select(r => r.EntryId));
            if (!page.HasMore) break;
            from = page.Rows[^1].LastModified.AddSeconds(1);
        }
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());

        var rawDesc = rawAsc.AsEnumerable().Reverse().ToList();
        seen.Clear();
        var to = new DateTimeOffset(Base.AddHours(1), TimeSpan.Zero);
        var lower = new DateTimeOffset(Base, TimeSpan.Zero);
        while (true)
        {
            var page = MailTableQueries.Range(rawDesc, lower, to, 2);
            seen.AddRange(page.Rows.Select(r => r.EntryId));
            if (!page.HasMore) break;
            to = page.Rows[^1].EventTime;
        }
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    // ---- thread columns (schema 2) ----

    private static object?[] Raw10(object? irt, object? topic, object? marker, string? id = "ID") =>
        [id, DBNull.Value, Utc("2026-03-01T10:00:00"), true, null, "IPM.Note", null, irt, topic, marker];

    [Fact]
    public void ThreadColumns_extend_Columns()
    {
        var cols = MailTable.ThreadColumns(EventTimeField.Received);
        Assert.Equal(10, cols.Count);
        Assert.Equal(MailTable.Columns(EventTimeField.Received), cols.Take(7).ToArray());
        Assert.Equal(MailTable.LongTermEntryIdProp, cols[6]);
        Assert.Equal("http://schemas.microsoft.com/mapi/proptag/0x1042001F", cols[7]);
        Assert.Equal("http://schemas.microsoft.com/mapi/proptag/0x0070001F", cols[8]);
        Assert.Equal(DraftMarker.PropertyName, cols[9]);
        Assert.Equal(cols.Skip(7).ToArray(), MailTable.ThreadPropertyNames);
    }

    [Fact]
    public void ToRow_with_ten_values_carries_thread_meta()
    {
        var values = Raw10("<a>", " Topic ", "TOOL");
        values[6] = new byte[] { 0xAB, 0xCD };
        var row = MailTable.ToRow(values)!;
        Assert.Equal("ABCD", row.EntryId);
        Assert.Equal(new MailThreadMeta("<a>", "Topic", "tool"), row.Thread);
    }

    [Fact]
    public void ToRow_seven_values_has_no_thread_and_eight_throws()
    {
        var seven = new object?[] { "id", DBNull.Value, Utc("2026-03-01T10:00:00"), false, null, "IPM.Note", null };
        Assert.Null(MailTable.ToRow(seven)!.Thread);
        var ex = Assert.Throws<ArgumentException>(() => MailTable.ToRow([.. seven, null]));
        Assert.Contains("10 with the thread columns", ex.Message);
    }

    [Fact]
    public void ToRow_ten_values_with_error_values_gives_Empty()
    {
        var row = MailTable.ToRow(Raw10((int)-2147221233, DBNull.Value, new byte[] { 1 }))!;
        Assert.Equal(MailThreadMeta.Empty, row.Thread);
    }

    [Fact]
    public void FromValues_normalizes()
    {
        Assert.Equal(MailThreadMeta.Empty, MailThreadMeta.FromValues("  ", "", null));
        Assert.Equal(new MailThreadMeta("<x>", "T", "ai"), MailThreadMeta.FromValues("<x>", "T", "ai"));
        Assert.Equal(MailThreadMeta.Empty, MailThreadMeta.FromValues(1, 2, 3));
    }

    [Fact]
    public void Range_keeps_thread_meta_on_ten_value_rows()
    {
        var from = U("2026-03-01T00:00:00Z");
        var to = U("2026-03-10T00:00:00Z");
        var raw = new[] { Raw10("<a>", "T1", "ai", "one"), Raw10(null, "T2", null, "two") };
        var page = MailTableQueries.Range(raw, from, to);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.NotNull(r.Thread));
        Assert.Equal("<a>", page.Rows[0].Thread!.InReplyTo);
    }
}

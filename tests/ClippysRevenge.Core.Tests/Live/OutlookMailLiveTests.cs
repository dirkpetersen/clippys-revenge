using System.Runtime.InteropServices;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Read-only live checks (SPEC §15). Run only with CLIPPY_LIVE=1 and Outlook running. Never writes anything.
/// All COM work runs on the fixture's worker (and so under its watchdog); assertions run on the test thread.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class OutlookMailLiveTests(LiveOutlook fx)
{
    private static List<object?[]> ReadRows(dynamic table)
    {
        var rows = new List<object?[]>();
        while (!(bool)table.EndOfTable)
        {
            var arr = (object[,])table.GetArray(200);
            for (var r = arr.GetLowerBound(0); r <= arr.GetUpperBound(0); r++)
            {
                var row = new object?[arr.GetLength(1)];
                for (var c = 0; c < row.Length; c++) row[c] = arr[r, c + arr.GetLowerBound(1)];
                rows.Add(row);
            }
        }
        return rows;
    }

    private static dynamic Table(dynamic inbox, string filter, EventTimeField field, string? sort = null, bool desc = false)
    {
        dynamic table = inbox.GetTable(filter, 0);
        dynamic cols = table.Columns;
        try
        {
            cols.RemoveAll();
            foreach (var c in MailTable.Columns(field)) cols.Add(c);
        }
        finally
        {
            Marshal.ReleaseComObject(cols);
        }
        if (sort is not null) table.Sort(sort, desc);
        return table;
    }

    private static object?[]? ReadOne(dynamic table)
    {
        var arr = (object[,])table.GetArray(1);
        if (arr.GetLength(0) == 0) return null;
        var row = new object?[arr.GetLength(1)];
        for (var c = 0; c < row.Length; c++) row[c] = arr[arr.GetLowerBound(0), c + arr.GetLowerBound(1)];
        return row;
    }

    private sealed record DaslData(List<object?[]> RowsA, List<object?[]> RowsB, int Future, int Wide);

    [Fact(Timeout = 180_000)]
    public async Task Dasl_helper_parses_in_this_locale()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-30);

        var data = await fx.RunAsync<DaslData?>("the DASL check", app =>
        {
            dynamic? session = null, inbox = null;
            try
            {
                session = app.Session;
                inbox = session.GetDefaultFolder(6);

                var t1 = Table(inbox, DaslFilter.Since(EventTimeField.Received, since), EventTimeField.Received);
                List<object?[]> rowsA;
                try { rowsA = ReadRows(t1); } finally { Marshal.ReleaseComObject(t1); }
                if (rowsA.Count == 0) return null;

                var t2 = Table(inbox, "@SQL=\"" + MailTable.MessageClassProp + "\" LIKE 'IPM.Note%'", EventTimeField.Received,
                    MailTable.SortReceived, true);
                var rowsB = new List<object?[]>();
                try
                {
                    while (!(bool)t2.EndOfTable)
                    {
                        var chunk = ReadOne(t2);
                        if (chunk is null) break;
                        rowsB.Add(chunk);
                        if (MailTable.ToRow(chunk) is { } r && r.EventTime < now.AddDays(-31)) break;
                    }
                }
                finally { Marshal.ReleaseComObject(t2); }

                var t3 = Table(inbox, DaslFilter.Since(EventTimeField.Received, now.AddDays(3)), EventTimeField.Received);
                int future;
                try { future = ReadRows(t3).Count; } finally { Marshal.ReleaseComObject(t3); }

                var t4 = Table(inbox, DaslFilter.Since(EventTimeField.Received, now.AddDays(-3650)), EventTimeField.Received);
                int wide;
                try { wide = ReadRows(t4).Count; } finally { Marshal.ReleaseComObject(t4); }

                return new DaslData(rowsA, rowsB, future, wide);
            }
            finally
            {
                if (inbox is not null) Marshal.ReleaseComObject(inbox);
                if (session is not null) Marshal.ReleaseComObject(session);
            }
        });

        if (data is null) Assert.Skip("No mail in the Inbox in the last 30 days.");
        var a = MailTableQueries.EntryIds(data.RowsA, since).Select(s => s.EntryId).ToHashSet();
        var b = MailTableQueries.EntryIds(data.RowsB, since).Select(s => s.EntryId).ToHashSet();
        Assert.True(a.SetEquals(b), $"culture {System.Globalization.CultureInfo.CurrentCulture.Name}: {a.Count} vs {b.Count}");
        Assert.Equal(0, data.Future);
        Assert.True(data.Wide >= data.RowsA.Count);
    }

    private sealed record DateRow(string EntryId, DateTimeOffset EventTime, DateTimeOffset LastModified,
        DateTimeOffset? Received, DateTimeOffset? Modified);

    [Fact(Timeout = 180_000)]
    public async Task Table_dates_are_utc()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-30);

        var rows = await fx.RunAsync<List<DateRow>>("the date check", app =>
        {
            dynamic? session = null, inbox = null;
            try
            {
                session = app.Session;
                inbox = session.GetDefaultFolder(6);
                var table = Table(inbox, DaslFilter.Range(EventTimeField.Received, from, now), EventTimeField.Received,
                    MailTable.SortReceived, true);
                List<object?[]> raw;
                try { raw = ReadRows(table); } finally { Marshal.ReleaseComObject(table); }
                var page = MailTableQueries.Range(raw, from, now);

                var result = new List<DateRow>();
                foreach (var row in page.Rows.Take(5))
                {
                    dynamic item = session.GetItemFromID(row.EntryId);
                    try
                    {
                        result.Add(new DateRow(row.EntryId, row.EventTime, row.LastModified,
                            OutlookDates.FromLocal((DateTime)item.ReceivedTime, TimeZoneInfo.Local),
                            OutlookDates.FromLocal((DateTime)item.LastModificationTime, TimeZoneInfo.Local)));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
                return result;
            }
            finally
            {
                if (inbox is not null) Marshal.ReleaseComObject(inbox);
                if (session is not null) Marshal.ReleaseComObject(session);
            }
        });

        if (rows.Count == 0) Assert.Skip("No mail in the Inbox in the last 30 days.");
        foreach (var row in rows)
        {
            Assert.True(Math.Abs((row.Received!.Value - row.EventTime).TotalSeconds) <= 1);
            Assert.True(Math.Abs((row.Modified!.Value - row.LastModified).TotalSeconds) <= 1);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Sort_keys_are_accepted()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;

        var (changed, range) = await fx.RunAsync<(List<object?[]>, List<object?[]>)>("the sort-key check", app =>
        {
            dynamic? session = null, inbox = null;
            try
            {
                session = app.Session;
                inbox = session.GetDefaultFolder(6);

                var t1 = Table(inbox, DaslFilter.Changed(EventTimeField.Received, now.AddDays(-30), now.AddDays(-365)),
                    EventTimeField.Received, MailTable.SortLastModified, false);
                List<object?[]> c;
                try { c = ReadRows(t1); } finally { Marshal.ReleaseComObject(t1); }

                var t2 = Table(inbox, DaslFilter.Range(EventTimeField.Received, now.AddDays(-30), now),
                    EventTimeField.Received, MailTable.SortReceived, true);
                List<object?[]> r;
                try { r = ReadRows(t2); } finally { Marshal.ReleaseComObject(t2); }
                return (c, r);
            }
            finally
            {
                if (inbox is not null) Marshal.ReleaseComObject(inbox);
                if (session is not null) Marshal.ReleaseComObject(session);
            }
        });

        _ = MailTableQueries.Changed(changed, now.AddDays(-30), now.AddDays(-365), MailTablePager.MaxPageRows);
        _ = MailTableQueries.Range(range, now.AddDays(-30), now, MailTablePager.MaxPageRows);
    }

    [Fact(Timeout = 180_000)]
    public async Task Thread_columns_table_works()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var page = await fx.RunAsync("reading thread columns", app =>
        {
            var rf = fx.Mail.ResolveFolder(app, "", "Inbox");
            return fx.Mail.EnumerateThreadMeta(app, rf, now.AddDays(-30), now.AddMinutes(1), 50);
        });
        if (page.Rows.Count == 0) Assert.Skip(SkipTexts.NoData("Inbox mail in the last 30 days"));
        Assert.All(page.Rows, r => Assert.NotNull(r.Thread));
        fx.Result("thread-columns",
            $"rows={page.Rows.Count}, withTopic={page.Rows.Count(r => r.Thread!.ConversationTopic is not null)}, withIrt={page.Rows.Count(r => r.Thread!.InReplyTo is not null)}");
    }
}

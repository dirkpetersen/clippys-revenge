using System.Runtime.InteropServices;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

/// <summary>Read-only live checks (SPEC §15). Run only with CLIPPY_LIVE=1 and Outlook running. Never writes anything.</summary>
[Trait("live", "outlook")]
[Trait("os", "windows")]
public class OutlookMailLiveTests
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    private static void RequireLive() =>
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CLIPPY_LIVE") == "1",
            "Set CLIPPY_LIVE=1 with Outlook running.");

    private static dynamic AttachOrSkip()
    {
        RequireLive();
        if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0) Assert.Skip("Outlook is not registered.");
        if (GetActiveObject(ref clsid, IntPtr.Zero, out var obj) != 0 || obj is null) Assert.Skip("Outlook is not running.");
        return obj;
    }

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
        dynamic table = inbox.GetTable(filter, 1);
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

    [Fact]
    public void Dasl_helper_parses_in_this_locale()
    {
        var app = AttachOrSkip();
        dynamic? session = null, inbox = null;
        try
        {
            session = app.Session;
            inbox = session.GetDefaultFolder(6);
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-30);

            var t1 = Table(inbox, DaslFilter.Since(EventTimeField.Received, since), EventTimeField.Received);
            List<object?[]> rowsA;
            try { rowsA = ReadRows(t1); } finally { Marshal.ReleaseComObject(t1); }
            var a = MailTableQueries.EntryIds(rowsA, since).Select(s => s.EntryId).ToHashSet();

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
            var b = MailTableQueries.EntryIds(rowsB, since).Select(s => s.EntryId).ToHashSet();
            Assert.True(a.SetEquals(b), $"culture {System.Globalization.CultureInfo.CurrentCulture.Name}: {a.Count} vs {b.Count}");

            var t3 = Table(inbox, DaslFilter.Since(EventTimeField.Received, now.AddDays(3)), EventTimeField.Received);
            try { Assert.Empty(ReadRows(t3)); } finally { Marshal.ReleaseComObject(t3); }

            var t4 = Table(inbox, DaslFilter.Since(EventTimeField.Received, now.AddDays(-3650)), EventTimeField.Received);
            int wide;
            try { wide = ReadRows(t4).Count; } finally { Marshal.ReleaseComObject(t4); }
            Assert.True(wide >= rowsA.Count);
        }
        finally
        {
            if (inbox is not null) Marshal.ReleaseComObject(inbox);
            if (session is not null) Marshal.ReleaseComObject(session);
            Marshal.ReleaseComObject(app);
        }
    }

    private static object?[]? ReadOne(dynamic table)
    {
        var arr = (object[,])table.GetArray(1);
        if (arr.GetLength(0) == 0) return null;
        var row = new object?[arr.GetLength(1)];
        for (var c = 0; c < row.Length; c++) row[c] = arr[arr.GetLowerBound(0), c + arr.GetLowerBound(1)];
        return row;
    }

    [Fact]
    public void Table_dates_are_utc()
    {
        var app = AttachOrSkip();
        dynamic? session = null, inbox = null;
        try
        {
            session = app.Session;
            inbox = session.GetDefaultFolder(6);
            var now = DateTimeOffset.UtcNow;
            var from = now.AddDays(-30);
            var table = Table(inbox, DaslFilter.Range(EventTimeField.Received, from, now), EventTimeField.Received,
                MailTable.SortReceived, true);
            List<object?[]> raw;
            try { raw = ReadRows(table); } finally { Marshal.ReleaseComObject(table); }
            var page = MailTableQueries.Range(raw, from, now);
            if (page.Rows.Count == 0) Assert.Skip("No mail in the Inbox in the last 30 days.");

            foreach (var row in page.Rows.Take(5))
            {
                dynamic item = session.GetItemFromID(row.EntryId);
                try
                {
                    var received = OutlookDates.FromLocal((DateTime)item.ReceivedTime, TimeZoneInfo.Local)!.Value;
                    var modified = OutlookDates.FromLocal((DateTime)item.LastModificationTime, TimeZoneInfo.Local)!.Value;
                    Assert.True(Math.Abs((received - row.EventTime).TotalSeconds) <= 1);
                    Assert.True(Math.Abs((modified - row.LastModified).TotalSeconds) <= 1);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (inbox is not null) Marshal.ReleaseComObject(inbox);
            if (session is not null) Marshal.ReleaseComObject(session);
            Marshal.ReleaseComObject(app);
        }
    }

    [Fact]
    public void Sort_keys_are_accepted()
    {
        var app = AttachOrSkip();
        dynamic? session = null, inbox = null;
        try
        {
            session = app.Session;
            inbox = session.GetDefaultFolder(6);
            var now = DateTimeOffset.UtcNow;

            var t1 = Table(inbox, DaslFilter.Changed(EventTimeField.Received, now.AddDays(-30), now.AddDays(-365)),
                EventTimeField.Received, MailTable.SortLastModified, false);
            List<object?[]> changed;
            try { changed = ReadRows(t1); } finally { Marshal.ReleaseComObject(t1); }
            _ = MailTableQueries.Changed(changed, now.AddDays(-30), now.AddDays(-365), MailTablePager.MaxPageRows);

            var t2 = Table(inbox, DaslFilter.Range(EventTimeField.Received, now.AddDays(-30), now),
                EventTimeField.Received, MailTable.SortReceived, true);
            List<object?[]> range;
            try { range = ReadRows(t2); } finally { Marshal.ReleaseComObject(t2); }
            _ = MailTableQueries.Range(range, now.AddDays(-30), now, MailTablePager.MaxPageRows);
        }
        finally
        {
            if (inbox is not null) Marshal.ReleaseComObject(inbox);
            if (session is not null) Marshal.ReleaseComObject(session);
            Marshal.ReleaseComObject(app);
        }
    }
}

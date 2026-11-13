using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Table paging, one sync into a temp index, ReadForIndex, the get_message fallback, read state (SPEC §8, §10, §11.2, §18).</summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveMailReadTests(LiveOutlook fx)
{
    private async Task<ResolvedFolder> InboxAsync() =>
        await fx.RunAsync("resolving Inbox", app => fx.Mail.ResolveFolder(app, "", "Inbox"));

    /// <summary>The indexed rows of the own mailbox, newest first, at most <paramref name="max"/>.</summary>
    private List<MailboxHit> IndexedRows(int max, Func<SearchFilter, SearchFilter>? shape = null)
    {
        var rows = new List<MailboxHit>();
        var offset = 0;
        while (rows.Count < max)
        {
            var filter = new SearchFilter { Limit = Math.Min(200, max - rows.Count), Offset = offset };
            if (shape is not null) filter = shape(filter);
            var page = fx.Indexes.Search(filter, "");
            rows.AddRange(page);
            if (page.Count < filter.Limit) break;
            offset += page.Count;
        }
        return rows;
    }

    private async Task<List<MailboxHit>> SyncedRowsAsync(int max, Func<SearchFilter, SearchFilter>? shape = null)
    {
        await fx.EnsureSyncedAsync();
        var rows = IndexedRows(max, shape);
        if (rows.Count == 0) Assert.Skip(SkipTexts.NoData("synced Inbox mail in the last month"));
        return rows;
    }

    [Fact(Timeout = 180_000)]
    public async Task Range_pages_are_disjoint_and_ordered()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var inbox = await InboxAsync();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-30);
        var to = now;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pageNo = 0;
        for (; pageNo < 6; pageNo++)
        {
            var (f, t) = (from, to);
            var page = await fx.RunAsync("paging the Inbox", app => fx.Mail.EnumerateRange(app, inbox, f, t, 25));
            if (pageNo == 0 && page.Rows.Count == 0) Assert.Skip(SkipTexts.NoData("Inbox mail in the last 30 days"));
            Assert.True(page.Rows.Count <= 25 || page.Rows.Count > 0, "page size");
            DateTimeOffset? previous = null;
            foreach (var r in page.Rows)
            {
                Assert.True(seen.Add(r.EntryId), $"Duplicate EntryId across pages: {r.EntryId}");
                if (previous is not null) Assert.True(r.EventTime <= previous, "EventTime must not increase within a page.");
                previous = r.EventTime;
                Assert.True(r.EventTime >= OutlookDates.TruncateToSecond(from) && r.EventTime < t,
                    $"Row {r.EventTime:o} is outside [{from:o}, {t:o}).");
            }
            if (!page.HasMore || page.Rows.Count == 0) break;
            to = page.Rows[^1].EventTime;
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Changed_pages_are_ascending()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var inbox = await InboxAsync();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-365);
        var modifiedFrom = now.AddDays(-30);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? last = null;
        for (var pageNo = 0; pageNo < 4; pageNo++)
        {
            var from = modifiedFrom;
            var page = await fx.RunAsync("paging changed mail", app => fx.Mail.EnumerateChanged(app, inbox, from, cutoff, 25));
            foreach (var r in page.Rows)
            {
                Assert.True(seen.Add(r.EntryId), $"Duplicate EntryId across pages: {r.EntryId}");
                if (last is not null) Assert.True(r.LastModified >= last, "LastModified must not decrease.");
                last = r.LastModified;
                Assert.True(r.LastModified >= OutlookDates.TruncateToSecond(from), $"Row {r.LastModified:o} is before {from:o}.");
            }
            if (!page.HasMore || page.Rows.Count == 0) break;
            modifiedFrom = page.Rows[^1].LastModified.AddSeconds(1);
        }
    }

    private async Task<MailboxSyncResult?> SyncOnceAsync(CancellationToken ct)
    {
        var engine = new SyncEngine(new OutlookMailSource(fx.Worker, fx.Stores, fx.Mail), fx.Hub, logger: null, reconcile: null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            return await engine.SyncMailboxAsync(new MailboxPlan("", ["Inbox"], 1), fx.Indexes.For(""), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static int Upserted(MailboxSyncResult? r) => r?.Folders.Sum(f => f.Upserted) ?? 0;

    [Fact(Timeout = 600_000)]
    public async Task Sync_end_to_end_into_temp_index()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var outcome = await fx.EnsureSyncedAsync();
        if (outcome.IndexedCount == 0 && !outcome.TimedOut)
        {
            var inbox = await InboxAsync();
            var probe = await fx.RunAsync("probing the Inbox", app =>
                fx.Mail.EnumerateRange(app, inbox, outcome.CutoffUtc.AddDays(1), outcome.StartedUtc, 1));
            if (probe.Rows.Count == 0) Assert.Skip(SkipTexts.NoData("Inbox mail in the last month"));
        }

        var index = fx.Indexes.For("");
        Assert.StartsWith(fx.TempRoot, index.DbPath, StringComparison.OrdinalIgnoreCase);

        foreach (var hit in IndexedRows(1000))
        {
            Assert.Equal("Inbox", hit.Email.Folder);
            Assert.True(hit.Email.Date >= outcome.CutoffUtc.AddDays(-1), $"Indexed row {hit.Email.Date:o} is older than the retention cutoff.");
        }

        if (!outcome.TimedOut)
        {
            var inbox = await InboxAsync();
            var upper = outcome.StartedUtc;
            for (var guard = 0; guard < 200; guard++)
            {
                var u = upper;
                var page = await fx.RunAsync("listing the Inbox for the sync check",
                    app => fx.Mail.EnumerateRange(app, inbox, outcome.CutoffUtc.AddDays(1), u));
                foreach (var r in page.Rows)
                    Assert.True(index.Get(r.EntryId) is not null, $"Inbox mail {r.EntryId} ({r.EventTime:o}) is missing from the index.");
                if (!page.HasMore || page.Rows.Count == 0) break;
                upper = page.Rows[^1].EventTime;
            }
        }

        var first = Upserted(outcome.Result);
        var second = await SyncOnceAsync(TestContext.Current.CancellationToken);
        var secondUpserted = Upserted(second);
        fx.Result("sync-second-run", $"first upserted {first}, second upserted {secondUpserted}, second timed out={second is null}");
        if (!outcome.TimedOut && second is not null)
            Assert.True(secondUpserted <= Math.Max(25, first / 10),
                $"The second sync re-read too much: {secondUpserted} upserts after a first run with {first}.");
    }

    [Fact(Timeout = 180_000)]
    public async Task ReadForIndex_matches_live_item()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var rows = (await SyncedRowsAsync(5)).Take(5).ToList();
        foreach (var hit in rows)
        {
            var id = hit.Email.Id;
            var live = (await fx.RunAsync("reading a mail item", app => fx.Mail.ReadById(app, id))).Email;
            Assert.Equal(id, live.EntryId);
            Assert.Equal(hit.Email.Subject, live.Subject ?? "");
            Assert.True(Math.Abs((live.EventTime - hit.Email.Date).TotalSeconds) <= 1, "EventTime differs from the index.");
            var detail = fx.Indexes.For("").Get(id);
            Assert.NotNull(detail);
            Assert.Equal(detail.Unread, live.Unread);

            Assert.True(live.SenderEmail is null || MailAddresses.LooksLikeSmtp(live.SenderEmail), "SenderEmail: " + live.SenderEmail);
            Assert.False(live.SenderEmail?.StartsWith("/o=", StringComparison.OrdinalIgnoreCase) ?? false, "SenderEmail is an Exchange DN.");
            foreach (var list in new[] { live.To, live.Cc })
            {
                if (string.IsNullOrWhiteSpace(list)) continue;
                foreach (var rec in Recipients.Parse(list))
                    Assert.True(rec.Email.Length == 0 || MailAddresses.LooksLikeSmtp(rec.Email), "Recipient: " + rec.Email);
            }
            Assert.Equal(live.Attachments.Count, live.Attachments.Select(a => a.Position).Distinct().Count());
            Assert.All(live.Attachments, a => Assert.False(string.IsNullOrWhiteSpace(a.FileName)));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task GetMessage_index_hit()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var hit = (await SyncedRowsAsync(1))[0];
        var dto = MailQueries.GetIndexed(fx.Indexes, TimeZoneInfo.Local, hit.Email.Id);
        Assert.NotNull(dto);
        Assert.Equal(hit.Email.Subject, dto.Subject);
        Assert.Equal("index", dto.Source);
    }

    [Fact(Timeout = 180_000)]
    public async Task GetMessage_live_fallback_when_not_indexed()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var id = (await SyncedRowsAsync(1))[0].Email.Id;
        using var index2 = new MailboxIndexes(Path.Combine(fx.TempRoot, "index2"), []);
        var mail2 = new OutlookMail(fx.Stores, index2, fx.Settings);
        var live = await fx.RunAsync("the live fallback", app => mail2.ReadById(app, id));
        Assert.Equal("", live.Mailbox);
        Assert.Equal(id, live.Email.EntryId);
    }

    [Fact(Timeout = 180_000)]
    public async Task GetMessage_finds_moved_item_by_message_id()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var rows = await SyncedRowsAsync(200);
        var index = fx.Indexes.For("");
        var pick = rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(index.Get(r.Email.Id)?.InternetMessageId));
        if (pick is null) Assert.Skip(SkipTexts.NoData("indexed email with an internet message id"));

        var realId = pick.Email.Id;
        var live = (await fx.RunAsync("reading a mail item", app => fx.Mail.ReadById(app, realId))).Email;
        var fakeId = realId[..^8] + "00000000";
        Assert.NotEqual(realId, fakeId);

        using var index2 = new MailboxIndexes(Path.Combine(fx.TempRoot, "index2"), []);
        index2.For("").UpsertBatch([live with { EntryId = fakeId }]);
        var mail2 = new OutlookMail(fx.Stores, index2, fx.Settings);
        var found = await fx.RunAsync("finding the moved item", app => mail2.ReadById(app, fakeId));
        Assert.Equal(realId, found.Email.EntryId);
    }

    [Fact(Timeout = 180_000)]
    public async Task GetMessage_unknown_id_throws_not_found()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        using var index2 = new MailboxIndexes(Path.Combine(fx.TempRoot, "index2"), []);
        var mail2 = new OutlookMail(fx.Stores, index2, fx.Settings);
        var id = "0000000000000000" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var ex = await Assert.ThrowsAsync<ItemNotFoundException>(() =>
            fx.RunAsync("looking up an unknown id", app => mail2.ReadById(app, id)));
        Assert.Equal($"Email \"{id}\" was not found in Outlook. It may have been deleted or moved to a folder that isn't indexed. Run search_emails again to get a current id.",
            ex.Message);
    }

    private static (bool Unread, DateTime Modified) ReadState(Outlook.Application app, string id)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(id);
            return (item.UnRead, item.LastModificationTime);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    /// <summary>Sets UnRead and releases the item. Never calls Save().</summary>
    private static void SetUnread(Outlook.Application app, string id, bool unread)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(id);
            item.UnRead = unread;
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Read_state_and_LastModificationTime()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var rows = await SyncedRowsAsync(1, f => f with { Unread = false });
        var hit = rows[0];
        var id = hit.Email.Id;

        bool? original = null;   // set on the worker thread BEFORE anything is changed
        var changed = false;
        try
        {
            var l0 = await fx.RunAsync("reading the read state", app =>
            {
                var s = ReadState(app, id);
                original = s.Unread;
                return s.Modified;
            });
            if (original != false)
            {
                fx.Result("read-state-bumps-lmt", "inconclusive: the newest indexed read mail is unread in Outlook now");
                return;
            }

            changed = true;
            await fx.RunAsync("marking unread", app => { SetUnread(app, id, true); return 0; });
            var afterSet = await fx.RunAsync("reading the read state", app => ReadState(app, id));
            if (!afterSet.Unread)
            {
                fx.Result("read-state-bumps-lmt", "inconclusive: UnRead did not persist without Save()");
                return;
            }

            await fx.RunAsync("marking read", app => { SetUnread(app, id, false); return 0; });
            var afterReset = await fx.RunAsync("reading the read state", app => ReadState(app, id));
            var bumped = afterSet.Modified > l0 || afterReset.Modified > afterSet.Modified;
            fx.Result("read-state-bumps-lmt", bumped ? "yes" : "no");
        }
        finally
        {
            if (original is bool orig && changed)
            {
                var restored = false;
                try
                {
                    await fx.Worker.RunAsync(app => { SetUnread(app, id, orig); return 0; }, WorkPriority.Interactive,
                        CancellationToken.None, TimeSpan.FromSeconds(30));
                    var now = await fx.Worker.RunAsync(app => ReadState(app, id), WorkPriority.Interactive,
                        CancellationToken.None, TimeSpan.FromSeconds(30));
                    restored = now.Unread == orig;
                }
                catch (Exception ex) when (ex is OutlookUnavailableException or COMException or ModelContextProtocol.McpException)
                {
                    restored = false;
                }
                if (!restored)
                    fx.Leftover($"own|Inbox|{hit.Email.Subject} (left unread)");
                Assert.True(restored, "The original read state could not be restored.");
            }
        }
    }
}

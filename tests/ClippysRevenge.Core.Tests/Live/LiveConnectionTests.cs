using System.Diagnostics;
using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Attach-only, preflight, stores, owner lookup and folders (SPEC §4, §8). Read only.</summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveConnectionTests(LiveOutlook fx)
{
    private static HashSet<int> OutlookPids()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("OUTLOOK"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    /// <summary>Calls the production attacher on a fresh STA thread; releases what it got.</summary>
    private static bool TryAttachOnSta()
    {
        var attached = false;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var attacher = new OutlookAttacher();
                var app = attacher.TryAttach();
                if (app is not null)
                {
                    attached = true;
                    attacher.Release(app);
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("TryAttach threw: " + error.Message, error);
        return attached;
    }

    [Fact(Timeout = 180_000)]
    public async Task Attach_never_launches_outlook()
    {
        // No RequireReadyAsync: this test must also run when Outlook is closed.
        Assert.SkipUnless(LiveOutlook.Enabled, SkipTexts.NotLive);
        var before = OutlookPids();
        var attached = TryAttachOnSta();
        if (before.Count == 0)
        {
            Assert.False(attached, "TryAttach returned an Outlook although no OUTLOOK process was running.");
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(OutlookPids());
        }
        else
        {
            Assert.True(attached, "OUTLOOK is running but TryAttach found no running object (is it the new Outlook, or elevated?).");
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(before.SetEquals(OutlookPids()), "The set of OUTLOOK processes changed during the attach.");
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Preflight_reports_connection_mode()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("unknown", fx.ConnectionMode);
        Assert.Equal(OutlookConnectionState.Available, fx.Worker.State);
    }

    [Fact(Timeout = 180_000)]
    public async Task Stores_list_has_own_mailbox()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var (list, storeIdFromGet) = await fx.RunAsync("the store list", app =>
        {
            var l = fx.Stores.List(app);
            var store = fx.Stores.Get(app, "");
            try { return (l, store.StoreID); }
            finally { Marshal.ReleaseComObject(store); }
        });
        var own = Assert.Single(list, s => s.IsOwn);
        Assert.False(string.IsNullOrWhiteSpace(own.StoreId));
        if (!string.IsNullOrEmpty(own.Smtp)) Assert.True(MailAddresses.LooksLikeSmtp(own.Smtp), "Own SMTP: " + own.Smtp);
        Assert.Equal(own.StoreId, storeIdFromGet);
    }

    [Fact(Timeout = 180_000)]
    public async Task Stores_list_has_shared_mailbox()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        if (fx.SharedMailbox is null) Assert.Skip(SkipTexts.NoShared);
        await fx.Stores.EnsureResolvedAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        var list = await fx.RunAsync("the store list", app => fx.Stores.List(app));
        Assert.NotNull(StoreCatalog.Find(list, fx.SharedMailbox));
    }

    [Fact(Timeout = 180_000)]
    public async Task Owner_smtp_lookup_is_bounded_offline()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var (id, known) = await fx.RunAsync("the store list", app =>
        {
            var own = fx.Stores.List(app).Single(s => s.IsOwn);
            return (own.StoreId, own.Smtp);
        });

        var sw = Stopwatch.StartNew();
        try
        {
            // Direct on the worker: a timeout is an expected outcome here.
            var smtp = await fx.Worker.RunAsync(app => fx.Stores.LookupOwnerSmtp(app, id), WorkPriority.Interactive, ct,
                OutlookStores.LookupTimeout);
            if (smtp is not null)
            {
                Assert.True(MailAddresses.LooksLikeSmtp(smtp), "Owner SMTP: " + smtp);
                if (!string.IsNullOrEmpty(known)) Assert.Equal(known, smtp, ignoreCase: true);
            }
            fx.Result("owner-smtp-lookup", $"{smtp ?? "null"} in {sw.ElapsedMilliseconds} ms");
        }
        catch (OutlookNotRespondingException)
        {
            Assert.True(sw.Elapsed <= OutlookStores.LookupTimeout + TimeSpan.FromSeconds(5),
                $"The owner lookup took {sw.Elapsed.TotalSeconds:0.0} s to give up; the limit is 10 s + 5 s.");
            var recovering = Stopwatch.StartNew();
            while (fx.Worker.State != OutlookConnectionState.Available && recovering.Elapsed < TimeSpan.FromSeconds(90))
                await Task.Delay(500, ct);
            if (fx.Worker.State != OutlookConnectionState.Available)
                Assert.Skip(SkipTexts.NotResponding("recovery after the owner lookup", 90));
            var listing = Stopwatch.StartNew();
            await fx.RunAsync("the store list after recovery", app => fx.Stores.List(app), TimeSpan.FromSeconds(30));
            Assert.True(listing.Elapsed < TimeSpan.FromSeconds(30), "Stores.List was slow after the owner lookup timed out.");
            fx.Result("owner-smtp-lookup", $"timed out after 10 s, worker recovered in {recovering.Elapsed.TotalSeconds:0} s");
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Mail_folders_include_inbox_and_sent()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var folders = await fx.RunAsync("the folder list", app => fx.Stores.ListMailFolders(app, ""));
        Assert.Contains("Inbox", folders);
        Assert.Contains("Sent Items", folders);
        Assert.True(folders.Count <= 2000, "More than 2000 folders: " + folders.Count);

        var inbox = await fx.RunAsync("resolving Inbox", app => fx.Mail.ResolveFolder(app, "", "inbox"));
        Assert.Equal("Inbox", inbox.Path);
        Assert.Equal(EventTimeField.Received, inbox.TimeField);
        var sent = await fx.RunAsync("resolving Sent Items", app => fx.Mail.ResolveFolder(app, "", "sent items"));
        Assert.Equal(EventTimeField.Sent, sent.TimeField);

        var name = "No Such Folder " + fx.RunToken;
        var ex = await Assert.ThrowsAsync<MailFolderNotFoundException>(() =>
            fx.RunAsync("resolving a missing folder", app => fx.Mail.ResolveFolder(app, "", name)));
        Assert.Equal($"Folder \"{name}\" was not found in your own mailbox. Check the indexed folders in the Clippy tray settings.",
            ex.Message);
    }
}

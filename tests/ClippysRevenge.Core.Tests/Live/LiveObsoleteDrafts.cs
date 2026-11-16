using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Per-field view of the fingerprint inputs: name -> "len:n sha:first 12 hex of SHA-256(UTF-8)".
/// Keys exactly: subject, to, cc, bcc, html, categories, importance, attachments. Never holds the values.</summary>
internal sealed record FingerprintFields(IReadOnlyDictionary<string, string> Fields)
{
    public static readonly string[] Keys = ["subject", "to", "cc", "bcc", "html", "categories", "importance", "attachments"];

    /// <summary>Names whose entry differs, in the key order above. Empty = same.</summary>
    public IReadOnlyList<string> DiffFrom(FingerprintFields other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var diff = new List<string>();
        foreach (var key in Keys)
        {
            Fields.TryGetValue(key, out var mine);
            other.Fields.TryGetValue(key, out var theirs);
            if (!string.Equals(mine, theirs, StringComparison.Ordinal)) diff.Add(key);
        }
        return diff;
    }
}

/// <summary>A draft in Deleted Items found by its exact subject (worker thread only).</summary>
internal sealed record DeletedDraft(string EntryId, bool Sent, string? Marker, string? MessageClass);

/// <summary>Helpers of the M16 live tests (SPEC 23.11). Nothing here prints mail text or addresses.</summary>
internal static class LiveObsoleteDrafts
{
    public const int StabilitySeconds = 120;

    /// <summary>Creates the synthetic self-addressed source, tracks it, and checks Outlook can reply to it.</summary>
    public static async Task<(string Id, string StoreId)> CreateSourceAsync(LiveOutlook fx, LiveOutlook.Scope scope,
        Recipient me, string title, CancellationToken ct)
    {
        var storeId = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
        var subject = $"[{fx.RunToken}] {title}";
        var body = string.Join("\n",
            "Hi,",
            "",
            "This is a synthetic message for a live test. Nothing needs an answer.",
            "",
            "Thanks,",
            "Sam",
            "",
            fx.RunToken);
        var src = await new CreateDraftTool(fx.Settings,
                new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
            .RunAsync(new CreateDraftArgs(Recipients.Format([me]), subject, body), ct);
        scope.Track(new TrackedItem(TrackedKind.Draft, "", src.Id, storeId, subject));

        var id = src.Id;
        var (baselineHtml, baselineHResult) = await fx.RunAsync("checking Outlook's own reply",
            app => LiveDrafts.TryOwnReply(app, id, storeId));
        if (baselineHtml is null)
        {
            fx.Result("m16-baseline",
                $"skipped: reply to an unsent item failed (HResult 0x{baselineHResult.ToString("x8", CultureInfo.InvariantCulture)})");
            Assert.Skip("Outlook can't reply to an unsent test item on this profile, so the M16 live test can't run here. Nothing was sent.");
        }
        return (src.Id, storeId);
    }

    /// <summary>A reply draft through the production writer. Tracked at once, so a failed test still cleans it up once it is tagged.</summary>
    public static async Task<RepliedDraft> CreateReplyAsync(LiveOutlook fx, LiveOutlook.Scope scope, string sourceId,
        string storeId, string marker, WorkPriority priority, CancellationToken ct)
    {
        var plan = new ReplyPlan(sourceId, null, false,
            DraftBody.Fragment("<p>M16 live test " + fx.RunToken + "</p>", html: true, null), false, marker, priority);
        var made = await new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores).ReplyAsync(plan, ct);
        scope.Track(new TrackedItem(TrackedKind.Draft, "", made.EntryId, storeId, "[" + fx.RunToken + "] " + made.Subject));
        return made;
    }

    /// <summary>Prefixes "[RunToken] " and saves (only while not sent), so the token-checked cleanup can find the draft.</summary>
    public static Task TagAsync(LiveOutlook fx, string entryId) =>
        fx.RunAsync("tagging the draft", app => LiveDrafts.ReadDraft(fx, app, "", entryId, tag: true));

    /// <summary>Worker thread. The eight fingerprint inputs of the item, read like OutlookMail.ReadDraftFingerprint.</summary>
    public static FingerprintFields ReadFields(Outlook.MailItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var names = new List<string>();
        Outlook.Attachments? atts = null;
        try
        {
            atts = item.Attachments;
            var count = atts.Count;
            for (var i = 1; i <= count; i++)
            {
                Outlook.Attachment? a = null;
                try
                {
                    a = atts[i];
                    var name = a.FileName;
                    names.Add(string.IsNullOrEmpty(name) ? "?" : name);
                }
                finally
                {
                    LiveDrafts.Rel(a);
                }
            }
        }
        finally
        {
            LiveDrafts.Rel(atts);
        }
        var values = new (string Key, string? Value)[]
        {
            ("subject", item.Subject),
            ("to", item.To),
            ("cc", item.CC),
            ("bcc", item.BCC),
            ("html", item.HTMLBody),
            ("categories", item.Categories),
            ("importance", ((int)item.Importance).ToString(CultureInfo.InvariantCulture)),
            ("attachments", string.Join("\n", names)),
        };
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values) map[key] = Describe(value ?? "");
        return new FingerprintFields(map);
    }

    private static string Describe(string value) =>
        "len:" + value.Length.ToString(CultureInfo.InvariantCulture) + " sha:"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    /// <summary>Opens the item by id (own store) and returns ReadFields; releases everything.</summary>
    public static Task<FingerprintFields> FieldsAsync(LiveOutlook fx, string entryId, string storeId) =>
        fx.RunAsync("reading the draft fields", app => WithItem(app, entryId, storeId, ReadFields));

    /// <summary>Worker thread. Opens the mail item, runs f, releases the item and the session.</summary>
    private static T WithItem<T>(Outlook.Application app, string entryId, string storeId, Func<Outlook.MailItem, T> f)
    {
        Outlook.NameSpace? session = null;
        object? obj = null;
        try
        {
            session = app.Session;
            obj = session.GetItemFromID(entryId, storeId);
            return f((Outlook.MailItem)obj);
        }
        finally
        {
            LiveDrafts.Rel(obj);
            LiveDrafts.Rel(session);
        }
    }

    /// <summary>A plain Save() of the unchanged draft, as an autosave would do. The test's own draft only.</summary>
    public static Task ResaveAsync(LiveOutlook fx, string entryId, string storeId) =>
        fx.RunAsync("saving the draft again", app => WithItem(app, entryId, storeId, item =>
        {
            if (!item.Sent) item.Save();
            return true;
        }));

    /// <summary>Appends a paragraph before the closing body tag (or at the end) of HTMLBody and saves. The test's own draft only.</summary>
    public static Task EditBodyAsync(LiveOutlook fx, string entryId, string storeId) =>
        fx.RunAsync("editing the draft body", app => WithItem(app, entryId, storeId, item =>
        {
            if (item.Sent) return false;
            const string added = "<p>edited by the M16 live test</p>";
            var html = item.HTMLBody ?? "";
            var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            item.HTMLBody = at < 0 ? html + added : html.Insert(at, added);
            item.Save();
            return true;
        }));

    /// <summary>Items in the own store's Deleted Items whose subject equals exactSubject.</summary>
    public static Task<IReadOnlyList<DeletedDraft>> FindInDeletedItemsAsync(LiveOutlook fx, string exactSubject) =>
        fx.RunAsync("looking in Deleted Items", app => fx.OnFolder(app, "", Outlook.OlDefaultFolders.olFolderDeletedItems,
            folder => FindIn(fx, folder, exactSubject)));

    private static IReadOnlyList<DeletedDraft> FindIn(LiveOutlook fx, Outlook.MAPIFolder folder, string exactSubject)
    {
        var found = new List<DeletedDraft>();
        Outlook.Items? items = null, restricted = null;
        try
        {
            items = folder.Items;
            restricted = items.Restrict($"@SQL=\"urn:schemas:httpmail:subject\" LIKE '%{fx.RunToken}%'");
            object? item = restricted.GetFirst();
            while (item is not null)
            {
                try
                {
                    if (item is Outlook.MailItem mail && string.Equals(mail.Subject, exactSubject, StringComparison.Ordinal))
                        found.Add(new DeletedDraft(mail.EntryID, mail.Sent, LiveDrafts.ReadMarker(mail), mail.MessageClass));
                }
                finally
                {
                    LiveDrafts.Rel(item);
                }
                item = restricted.GetNext();
            }
        }
        finally
        {
            LiveDrafts.Rel(restricted);
            LiveDrafts.Rel(items);
        }
        return found;
    }

    /// <summary>Worker thread. A hidden inspector object for the item (no window is shown), kept until disposed;
    /// disposing closes it with olDiscard (the draft has no unsaved change) and releases it. Never throws from DisposeAsync.</summary>
    public static Task<IAsyncDisposable> HoldInspectorAsync(LiveOutlook fx, string entryId, string storeId) =>
        fx.RunAsync<IAsyncDisposable>("opening a hidden inspector", app =>
        {
            Outlook.NameSpace? session = null;
            Outlook.MailItem? item = null;
            Outlook.Inspector? inspector = null;
            try
            {
                session = app.Session;
                item = (Outlook.MailItem)session.GetItemFromID(entryId, storeId);
                inspector = item.GetInspector;
                var hold = new InspectorHold(fx, inspector, item);
                inspector = null;
                item = null;
                return hold;
            }
            finally
            {
                LiveDrafts.Rel(inspector);
                LiveDrafts.Rel(item);
                LiveDrafts.Rel(session);
            }
        });

    private sealed class InspectorHold(LiveOutlook fx, Outlook.Inspector inspector, Outlook.MailItem item) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await fx.Worker.RunAsync(_ =>
                {
                    try
                    {
                        inspector.Close(Outlook.OlInspectorClose.olDiscard);
                    }
                    catch (COMException)
                    {
                        // Nothing to close any more.
                    }
                    finally
                    {
                        LiveDrafts.Rel(inspector);
                        LiveDrafts.Rel(item);
                    }
                    return true;
                }, WorkPriority.Interactive, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LiveObsoleteDrafts inspector close: " + ex.Message);
            }
        }
    }
}

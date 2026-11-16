using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Shared helpers of the M19.8 live tests (SPEC 26.12 "Live"). Worker-thread helpers say so.</summary>
internal static class LiveAttachmentContent
{
    public const string PdfName = "Live facts.pdf";
    public const string DocxName = "Live agenda.docx";
    public const string SyncedDocxName = "Live notes.docx";
    public const string SyncedXlsxName = "Live budget.xlsx";

    /// <summary>Synthetic, never fetched: host passes the SPEC 26.3 host rule, ":w:" + "s" is an opaque share token.</summary>
    public const string UnsyncedLink = "https://contoso.sharepoint.com/:w:/s/clippy-live/EcLiPpYlIvE0tEsT1nG2hQ?e=Ab12Cd";

    /// <summary>Synthetic cloud URL for the olByReference probe. Never fetched.</summary>
    public const string SyntheticCloudUrl =
        "https://contoso.sharepoint.com/sites/clippy-live/Shared%20Documents/clippy-live%20cloud.docx";

    public const string CloudSubjectVariable = "CLIPPY_LIVE_CLOUD_SUBJECT";

    /// <summary>The synced-OneDrive test writes into the user's OneDrive, so it needs this on top of the live gate.</summary>
    public const string OneDriveVariable = "CLIPPY_LIVE_ONEDRIVE";

    private const string CloudSubjectPrefix = "clippy-live";

    /// <summary>prefix + "-" + 6 upper-case hex chars, e.g. "ORCHID-7F3A21".</summary>
    public static string NewFact(string prefix) => prefix + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    public static byte[] Pdf(string fact) =>
        PdfFixtures.Pages("Clippy live test document. Access code: " + fact + ". End of document.");

    public static byte[] Docx(string sentence) =>
        OoxmlFixtures.Docx(OoxmlFixtures.P("Clippy live test agenda.") + OoxmlFixtures.P("Meeting room: " + sentence + "."));

    public static byte[] Xlsx(string fact) =>
        OoxmlFixtures.Xlsx([("Budget", OoxmlFixtures.Row(1,
            OoxmlFixtures.InlineCell("A1", "Budget code"), OoxmlFixtures.InlineCell("B1", fact)))]);

    /// <summary>The production AiFiles over the live run's own folder (never the app's), then CleanUpAtStart as Program does.</summary>
    public static AiFiles NewAiFiles(LiveOutlook fx, ILogger? logger)
    {
        var files = new AiFiles(new AiFilesOptions
        {
            Folder = Path.Combine(fx.TempRoot, "ai-files"),
            EnsurePrivateFolder = PrivateFolder.Ensure,
            Logger = logger,
        });
        files.CleanUpAtStart();
        return files;
    }

    public static int CountEntries(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;

    /// <summary>True when the UTF-8 bytes of needle are in the index db, its -wal or its -journal file.</summary>
    public static bool IndexFilesContain(string dbPath, string needle)
    {
        var pattern = Encoding.UTF8.GetBytes(needle);
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-journal" })
        {
            if (!File.Exists(path)) continue;
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            if (bytes.AsSpan().IndexOf(pattern) >= 0) return true;
        }
        return false;
    }

    /// <summary>The first root the test may write in: not a folder scope, a "-my.sharepoint.com" host, first path segment "personal".</summary>
    public static SyncedRoot? OwnBusinessRoot(IReadOnlyList<SyncedRoot> roots)
    {
        foreach (var r in roots)
        {
            if (r.IsFolderScope) continue;
            if (!Uri.TryCreate(r.UrlPrefix, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.EndsWith("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase)) continue;
            var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(first, "personal", StringComparison.Ordinal)) return r;
        }
        return null;
    }

    public static string SyncedUrl(SyncedRoot root, bool rForm, params string[] segments)
    {
        var uri = new Uri(root.UrlPrefix);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .Concat(segments)
            .Select(Uri.EscapeDataString);
        var sb = new StringBuilder(uri.Scheme).Append("://").Append(uri.Authority);
        if (rForm) sb.Append("/:x:/r");
        return sb.Append('/').Append(string.Join('/', parts)).ToString();
    }

    /// <summary>A coarse class of the host, never the host itself.</summary>
    public static string HostClass(object? urlOrNull)
    {
        if (urlOrNull is not string s || string.IsNullOrWhiteSpace(s)) return "absent";
        if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "not-https";
        var host = uri.Host;
        if (host.EndsWith("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase)) return "sharepoint-my";
        if (host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)) return "sharepoint";
        if (host.Equals("d.docs.live.net", StringComparison.OrdinalIgnoreCase)) return "d.docs.live.net";
        if (host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase)) return "onedrive.live.com";
        if (host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase)) return "1drv.ms";
        return "other-https";
    }

    /// <summary>Worker thread. app.Inspectors.Count; releases the collection.</summary>
    public static int InspectorCount(Outlook.Application app)
    {
        Outlook.Inspectors? inspectors = null;
        try
        {
            inspectors = app.Inspectors;
            return inspectors.Count;
        }
        finally
        {
            LiveDrafts.Rel(inspectors);
        }
    }

    /// <summary>Worker thread. Attaches the inner item to the outer one as an embedded item and saves the outer item.
    /// Returns (int)Type of the added attachment. COMException (not "Outlook gone") propagates.</summary>
    public static int AttachItem(Outlook.Application app, string outerId, string innerId, string storeId)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? outer = null;
        Outlook.MailItem? inner = null;
        Outlook.Attachments? atts = null;
        Outlook.Attachment? added = null;
        try
        {
            session = app.Session;
            outer = (Outlook.MailItem)session.GetItemFromID(outerId, storeId);
            inner = (Outlook.MailItem)session.GetItemFromID(innerId, storeId);
            atts = outer.Attachments;
            added = atts.Add(inner, Outlook.OlAttachmentType.olEmbeddeditem, Type.Missing, Type.Missing);
            var type = (int)added.Type;
            outer.Save();
            return type;
        }
        finally
        {
            LiveDrafts.Rel(added);
            LiveDrafts.Rel(atts);
            LiveDrafts.Rel(inner);
            LiveDrafts.Rel(outer);
            LiveDrafts.Rel(session);
        }
    }

    /// <summary>Worker thread. Adds a by-reference attachment and saves. (true, 0) when added; (false, HResult) on a
    /// COMException that is not "Outlook gone".</summary>
    public static (bool Added, int HResult) TryAddByReference(Outlook.Application app, string outerId, string storeId,
        string url, string displayName)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? outer = null;
        Outlook.Attachments? atts = null;
        Outlook.Attachment? added = null;
        try
        {
            session = app.Session;
            outer = (Outlook.MailItem)session.GetItemFromID(outerId, storeId);
            atts = outer.Attachments;
            added = atts.Add(url, Outlook.OlAttachmentType.olByReference, Type.Missing, displayName);
            outer.Save();
            return (true, 0);
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return (false, ex.HResult);
        }
        finally
        {
            LiveDrafts.Rel(added);
            LiveDrafts.Rel(atts);
            LiveDrafts.Rel(outer);
            LiveDrafts.Rel(session);
        }
    }

    /// <summary>Worker thread. Read-only: per attachment, its position, type, PR_ATTACH_METHOD, size and the two URL
    /// properties, raw.</summary>
    public static IReadOnlyList<AttachmentProbe> Probe(Outlook.Application app, string entryId, string storeId)
    {
        var result = new List<AttachmentProbe>();
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        Outlook.Attachments? atts = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(entryId, storeId);
            atts = item.Attachments;
            var count = atts.Count;
            for (var i = 1; i <= count; i++)
            {
                Outlook.Attachment? a = null;
                Outlook.PropertyAccessor? pa = null;
                try
                {
                    a = atts[i];
                    pa = ComSafe.Try(() => a.PropertyAccessor);
                    var method = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachMethod));
                    var longName = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachLongFileName));
                    var longPath = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachLongPathName));
                    result.Add(new AttachmentProbe(
                        ComSafe.Try(() => a.Index), (int)a.Type, method is int m ? (int?)m : null,
                        ComSafe.Try<long?>(() => (long)a.Size), longName, longPath));
                }
                finally
                {
                    LiveDrafts.Rel(pa);
                    LiveDrafts.Rel(a);
                }
            }
        }
        finally
        {
            LiveDrafts.Rel(atts);
            LiveDrafts.Rel(item);
            LiveDrafts.Rel(session);
        }
        return result;
    }

    /// <summary>Worker thread. EntryID of the first own-Inbox item whose Subject equals subject, or null. Read-only.</summary>
    public static string? FindInboxBySubject(LiveOutlook fx, Outlook.Application app, string subject) =>
        fx.OnFolder(app, "", Outlook.OlDefaultFolders.olFolderInbox, folder =>
        {
            Outlook.Items? items = null, restricted = null;
            object? first = null;
            try
            {
                items = folder.Items;
                restricted = items.Restrict($"[Subject] = '{subject.Replace("'", "''", StringComparison.Ordinal)}'");
                first = restricted.GetFirst();
                return first is null ? null : (string?)((dynamic)first).EntryID;
            }
            finally
            {
                LiveDrafts.Rel(first);
                LiveDrafts.Rel(restricted);
                LiveDrafts.Rel(items);
            }
        });
}

internal sealed record AttachmentProbe(int Position, int Type, int? Method, long? Size, object? LongFileName,
    object? LongPathName);

/// <summary>Wraps an IAiMailbox. ReadEmailAsync for <c>entryId</c> replaces Email.SenderEmail with <c>ownSmtp</c> only when
/// Outlook gave no SMTP address; counts that in Filled. Every other member passes through.</summary>
internal sealed class SenderFillingMailbox(IAiMailbox inner, string entryId, string ownSmtp) : IAiMailbox
{
    private int _filled;

    public int Filled => Volatile.Read(ref _filled);

    public Task<CandidateFacts?> GetFactsAsync(string mailbox, string id, WorkPriority priority, CancellationToken ct) =>
        inner.GetFactsAsync(mailbox, id, priority, ct);

    public Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct) =>
        inner.GetIdentityAsync(priority, ct);

    public Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct) => inner.GetSelectedAsync(ct);

    public async Task<LiveEmail?> ReadEmailAsync(string id, CancellationToken ct)
    {
        var live = await inner.ReadEmailAsync(id, ct).ConfigureAwait(false);
        if (live is null || !string.Equals(id, entryId, StringComparison.Ordinal)
            || MailAddresses.LooksLikeSmtp(live.Email.SenderEmail))
            return live;
        Interlocked.Increment(ref _filled);
        return live with { Email = live.Email with { SenderEmail = ownSmtp } };
    }

    public Task<bool> ShowDraftsAsync(CancellationToken ct) => inner.ShowDraftsAsync(ct);

    public Task<AiDraftInfo?> ReadAiDraftAsync(string mailbox, string draftEntryId, WorkPriority priority, CancellationToken ct) =>
        inner.ReadAiDraftAsync(mailbox, draftEntryId, priority, ct);

    public Task<ObsoleteDraftMove> MoveObsoleteAiDraftAsync(string mailbox, string draftEntryId, string expectedFingerprint,
        WorkPriority priority, CancellationToken ct) =>
        inner.MoveObsoleteAiDraftAsync(mailbox, draftEntryId, expectedFingerprint, priority, ct);
}

/// <summary>Collects formatted messages of every level, thread-safe. Pass it as an ILogger.</summary>
internal sealed class LiveCapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate) return [.. _messages];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        lock (_gate) _messages.Add(text);
    }
}

/// <summary>One folder "&lt;MountPoint&gt;\&lt;RunToken&gt;" holding only files this run wrote. Removal is file by file,
/// then the folder itself, never recursive: a file the sync client put there makes the folder stay (a leftover).</summary>
internal sealed class SyncedTestFolder : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<string> _leftovers = [];
    private bool _disposed;

    /// <exception cref="InvalidOperationException">The folder already exists, or the mount isn't a fully qualified
    /// existing directory.</exception>
    public SyncedTestFolder(SyncedRoot root, string runToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!runToken.StartsWith("clippy-live-", StringComparison.Ordinal))
            throw new InvalidOperationException("The synced test folder name must start with clippy-live-.");
        if (!Path.IsPathFullyQualified(root.MountPoint) || !Directory.Exists(root.MountPoint))
            throw new InvalidOperationException("The OneDrive mount is not a fully qualified existing folder.");
        var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.MountPoint));
        Folder = Path.Combine(mount, runToken);
        var parent = Path.GetDirectoryName(Path.GetFullPath(Folder));
        if (parent is null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), mount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The synced test folder is not directly under the OneDrive mount.");
        if (Directory.Exists(Folder) || File.Exists(Folder))
            throw new InvalidOperationException("The synced test folder already exists.");
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    /// <summary>Markers "file" / "folder", never paths.</summary>
    public IReadOnlyList<string> Leftovers => _leftovers;

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Folder, name);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(bytes);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _files)
            if (!Retry(() => { if (File.Exists(f)) File.Delete(f); return !File.Exists(f); }))
                _leftovers.Add("file");
        if (!Retry(() => { if (Directory.Exists(Folder)) Directory.Delete(Folder, false); return !Directory.Exists(Folder); }))
            _leftovers.Add("folder");
    }

    private static bool Retry(Func<bool> step)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (step()) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // OneDrive may hold the file briefly while it uploads.
            }
            Thread.Sleep(500);
        }
        return false;
    }
}

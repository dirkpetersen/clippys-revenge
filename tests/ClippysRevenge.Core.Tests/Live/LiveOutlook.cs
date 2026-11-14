using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveOutlookCollection : ICollectionFixture<LiveOutlook>
{
    public const string Name = "live-outlook";
}

public enum TrackedKind { Draft, Meeting }

public sealed record TrackedItem(TrackedKind Kind, string Mailbox, string EntryId, string StoreId, string Subject);

public sealed record SentSnapshot(string Mailbox, int SentItems, int Outbox);

public sealed record LiveSyncOutcome(MailboxSyncResult? Result, bool TimedOut, long IndexedCount,
    DateTimeOffset StartedUtc, DateTimeOffset CutoffUtc);

public static class SkipTexts
{
    public const string NotLive = "Set CLIPPY_LIVE=1 with classic Outlook running (scripts/run-live-tests.ps1 does this).";
    public const string NotRunning = "Outlook is not running. Start classic Outlook and run the live tests again.";
    public static string NotResponding(string step, int seconds) =>
        $"Outlook did not answer within {seconds} s during {step}. It may be offline, busy or showing a dialog (for example a security prompt). Skipped; nothing was sent.";
    public static string NoData(string what) => $"No {what} found in the mailbox; nothing to test.";
    public const string NoShared = "Set CLIPPY_LIVE_SHARED to the SMTP address of a shared mailbox open in Outlook to run this test.";
}

/// <summary>One worker, one temp root and one ledger for the whole live run (SPEC §15).</summary>
public sealed class LiveOutlook : IAsyncLifetime
{
    private const int PreflightSeconds = 20;
    private const int RecoverySeconds = 60;

    private readonly object _ledgerLock = new();
    private readonly List<TrackedItem> _ledger = [];
    private readonly object _resultsLock = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly bool _resultsOverride;
    private LiveSyncOutcome? _sync;
    private string? _syncSkip;
    private bool _initialized;

    public static bool Enabled => OperatingSystem.IsWindows()
        && Environment.GetEnvironmentVariable("CLIPPY_LIVE") == "1";

    public string RunToken { get; }
    public string TempRoot { get; }
    public string AllowedDir { get; }
    public string? SharedMailbox { get; }
    public string ConnectionMode { get; private set; } = "unknown";
    public string? NotReadyReason { get; private set; }
    public ActivityHub Hub { get; }
    public Settings Settings { get; }
    public MailboxIndexes Indexes { get; private set; } = null!;
    public string ResultsPath { get; }

    internal OutlookWorker Worker { get; private set; } = null!;
    internal OutlookStores Stores { get; private set; } = null!;
    internal OutlookMail Mail { get; private set; } = null!;

    /// <summary>The constructor does no COM and no file I/O.</summary>
    public LiveOutlook()
    {
        RunToken = "clippy-live-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        TempRoot = Path.Combine(Path.GetTempPath(), "clippy-live", RunToken);
        AllowedDir = Path.Combine(TempRoot, "allowed");
        var shared = EmailAddress.Normalize(Environment.GetEnvironmentVariable("CLIPPY_LIVE_SHARED"));
        SharedMailbox = shared.Length == 0 ? null : shared;
        Hub = new ActivityHub();

        var s = Settings.CreateDefault(Path.Combine(TempRoot, "profile"));
        s.AttachmentDir = Path.Combine(TempRoot, "attachments");
        s.AllowedFolders = [AllowedDir];
        s.MeetingInviteMode = MeetingInviteMode.DraftOnly;
        s.RetentionMonths = 1;
        s.IndexedFolders = ["Inbox"];
        if (SharedMailbox is not null)
            s.SharedMailboxes = [new SharedMailbox { Smtp = SharedMailbox, Enabled = true, IndexedFolders = ["Inbox"] }];
        Settings = s;

        var fromEnv = Environment.GetEnvironmentVariable("CLIPPY_LIVE_RESULTS");
        _resultsOverride = !string.IsNullOrWhiteSpace(fromEnv);
        ResultsPath = _resultsOverride ? fromEnv! : Path.Combine(TempRoot, "results.txt");
    }

    public ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            NotReadyReason = SkipTexts.NotLive;
            return ValueTask.CompletedTask;
        }
        return new ValueTask(InitializeLiveAsync());
    }

    private async Task InitializeLiveAsync()
    {
        foreach (var d in new[] { TempRoot, AllowedDir, Settings.AttachmentDir, Path.Combine(TempRoot, "profile"),
                     Path.Combine(TempRoot, "index"), Path.Combine(TempRoot, "index2") })
            Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);

        Indexes = new MailboxIndexes(Path.Combine(TempRoot, "index"), SharedMailbox is null ? [] : [SharedMailbox]);
        foreach (var key in Indexes.Mailboxes)
            Assert.StartsWith(TempRoot, Indexes.For(key).DbPath, StringComparison.OrdinalIgnoreCase);

        Worker = new OutlookWorker(Hub, NullLogger.Instance);
        Worker.Start();
        Stores = new OutlookStores(Worker, () => Settings);
        Mail = new OutlookMail(Stores, Indexes, Settings);
        _initialized = true;

        try
        {
            var info = await Worker.RunAsync(app =>
            {
                Outlook.NameSpace? session = null;
                try
                {
                    session = app.Session;
                    int mode;
                    try { mode = (int)session.ExchangeConnectionMode; }
                    catch (COMException) { mode = -1; }
                    return (Name: app.Name, Version: app.Version, Mode: mode, Offline: session.Offline);
                }
                finally
                {
                    if (session is not null) Marshal.ReleaseComObject(session);
                }
            }, WorkPriority.Interactive, CancellationToken.None, TimeSpan.FromSeconds(PreflightSeconds)).ConfigureAwait(false);

            ConnectionMode = info.Mode < 0
                ? "not an Exchange profile (-1)"
                : $"{(Outlook.OlExchangeConnectionMode)info.Mode} ({info.Mode})";
            Result("connection-mode", $"{ConnectionMode}, Offline={info.Offline}, Outlook {info.Version}");
        }
        catch (OutlookNotRespondingException)
        {
            NotReadyReason = SkipTexts.NotResponding("the first call to Outlook", PreflightSeconds);
            return;
        }
        catch (OutlookUnavailableException)
        {
            NotReadyReason = SkipTexts.NotRunning;
            return;
        }

        try
        {
            var left = await Worker.RunAsync(app =>
            {
                var found = new List<string>();
                foreach (var which in new[] { Outlook.OlDefaultFolders.olFolderDrafts, Outlook.OlDefaultFolders.olFolderCalendar,
                             Outlook.OlDefaultFolders.olFolderDeletedItems })
                {
                    var name = which.ToString().Replace("olFolder", "", StringComparison.Ordinal);
                    foreach (var subject in OnFolder(app, "", which, f => SubjectsContaining(f, "clippy-live-")))
                        found.Add($"{name}|{subject}");
                }
                return found;
            }, WorkPriority.Interactive, CancellationToken.None, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            foreach (var l in left) WriteLine($"LIVE-LEFTOVER: previous run|{l}");
        }
        catch (Exception ex) when (ex is OutlookUnavailableException or COMException or ModelContextProtocol.McpException)
        {
            WriteLine("LIVE-RESULT: leftover-scan: skipped (" + ex.Message + ")");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_initialized)
            {
                List<TrackedItem> remaining;
                lock (_ledgerLock) remaining = [.. _ledger];
                if (remaining.Count > 0)
                {
                    await WaitForAvailableAsync(TimeSpan.FromSeconds(RecoverySeconds)).ConfigureAwait(false);
                    foreach (var item in remaining) await DeleteAsync(item).ConfigureAwait(false);
                }
                lock (_ledgerLock) remaining = [.. _ledger];
                foreach (var item in remaining)
                    WriteLine($"LIVE-LEFTOVER: {(item.Mailbox.Length == 0 ? "own" : item.Mailbox)}|"
                        + $"{(item.Kind == TrackedKind.Draft ? "Drafts" : "Calendar")}|{item.Subject}");
            }
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }

        try { Indexes?.Dispose(); } catch (Exception ex) { Swallow(ex); }
        try { _initialized = false; Worker?.Dispose(); } catch (Exception ex) { Swallow(ex); }
        try { Hub.Dispose(); } catch (Exception ex) { Swallow(ex); }

        try
        {
            if (Enabled && Directory.Exists(TempRoot))
            {
                if (!_resultsOverride && File.Exists(ResultsPath))
                {
                    var keep = Path.Combine(Path.GetTempPath(), "clippy-live", "last-results.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    File.Copy(ResultsPath, keep, overwrite: true);
                }
                // Only the exact per-run directory under %TEMP%\clippy-live is ever removed.
                if (TempRoot.Contains(RunToken, StringComparison.Ordinal))
                    Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    private static void Swallow(Exception ex) => System.Diagnostics.Debug.WriteLine("LiveOutlook dispose: " + ex.Message);

    private async Task WaitForAvailableAsync(TimeSpan max, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + max;
        while (Worker.State != OutlookConnectionState.Available && DateTime.UtcNow < until)
            await Task.Delay(500, ct).ConfigureAwait(false);
    }

    /// <summary>Skips (never fails, never hangs) when Outlook is not usable.</summary>
    public async Task RequireReadyAsync(CancellationToken ct = default)
    {
        if (NotReadyReason is not null) Assert.Skip(NotReadyReason);
        if (Worker.State is OutlookConnectionState.NotResponding or OutlookConnectionState.Unavailable)
        {
            await WaitForAvailableAsync(TimeSpan.FromSeconds(RecoverySeconds), ct).ConfigureAwait(false);
            if (Worker.State != OutlookConnectionState.Available)
                Assert.Skip(SkipTexts.NotResponding("waiting for Outlook", RecoverySeconds));
        }
    }

    /// <summary>Every COM call of the tests goes through here (and so through the watchdog).</summary>
    internal async Task<T> RunAsync<T>(string step, Func<Outlook.Application, T> work, TimeSpan? timeout = null)
    {
        try
        {
            return await Worker.RunAsync(work, WorkPriority.Interactive, TestContext.Current.CancellationToken, timeout)
                .ConfigureAwait(false);
        }
        catch (OutlookNotRespondingException)
        {
            Assert.Skip(SkipTexts.NotResponding(step, (int)(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds));
            throw;
        }
        catch (OutlookUnavailableException)
        {
            Assert.Skip(SkipTexts.NotRunning);
            throw;
        }
    }

    public void Track(TrackedItem item)
    {
        lock (_ledgerLock) _ledger.Add(item);
    }

    /// <summary>
    /// Deletes exactly the item whose entry id the test recorded, and only when its subject still carries
    /// "[RunToken] ". Never deletes by search. Never throws.
    /// </summary>
    public async Task<bool> DeleteAsync(TrackedItem item)
    {
        try
        {
            var done = await Worker.RunAsync(app => DeleteOne(app, item), WorkPriority.Interactive,
                CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (done)
                lock (_ledgerLock) _ledger.Remove(item);
            return done;
        }
        catch (Exception ex)
        {
            WriteLine($"LIVE-RESULT: cleanup-error: {item.Subject}: {ex.Message}");
            return false;
        }
    }

    private bool DeleteOne(Outlook.Application app, TrackedItem tracked)
    {
        Outlook.NameSpace? session = null;
        object? obj = null;
        try
        {
            session = app.Session;
            try
            {
                obj = session.GetItemFromID(tracked.EntryId, tracked.StoreId);
            }
            catch (COMException)
            {
                return PurgeDeleted(app, tracked.StoreId); // already gone from its folder; make sure Deleted Items is clean too
            }
            dynamic d = obj;
            string subject = d.Subject ?? "";
            if (!subject.StartsWith("[" + RunToken + "] ", StringComparison.Ordinal)
                || !tracked.Subject.StartsWith("[" + RunToken + "] ", StringComparison.Ordinal))
            {
                WriteLine($"LIVE-RESULT: cleanup-refused: subject of {tracked.EntryId} does not carry the run token");
                return false;
            }
            d.Delete();
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
        return PurgeDeleted(app, tracked.StoreId);
    }

    /// <summary>
    /// Delete() only moves an item to Deleted Items. Removes this run's items (subject starts with "[RunToken] ")
    /// from Deleted Items of the own store and of the tracked item's store. Worker thread only.
    /// </summary>
    private bool PurgeDeleted(Outlook.Application app, string storeId)
    {
        var prefix = "[" + RunToken + "] ";
        var ok = true;
        Outlook.NameSpace? session = null;
        var storeIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            session = app.Session;
            var targets = new List<Outlook.Store>();
            try
            {
                targets.Add(Stores.Get(app, ""));
                if (!string.IsNullOrEmpty(storeId))
                {
                    try { targets.Add(session.GetStoreFromID(storeId)); }
                    catch (COMException ex)
                    {
                        WriteLine($"LIVE-RESULT: cleanup-error: store of tracked item not found: {ex.Message}");
                        ok = false;
                    }
                }
                foreach (var store in targets)
                {
                    if (!storeIds.Add(store.StoreID)) continue;
                    Outlook.MAPIFolder? folder = null;
                    Outlook.Items? items = null, restricted = null;
                    var found = new List<object>();
                    try
                    {
                        folder = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDeletedItems);
                        items = folder.Items;
                        restricted = items.Restrict($"@SQL=\"urn:schemas:httpmail:subject\" LIKE '%{RunToken}%'");
                        object? item = restricted.GetFirst();
                        while (item is not null)
                        {
                            string subject;
                            try { subject = ((dynamic)item).Subject ?? ""; }
                            catch
                            {
                                if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                                throw;
                            }
                            if (subject.StartsWith(prefix, StringComparison.Ordinal)) found.Add(item);
                            else if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                            item = restricted.GetNext();
                        }
                        foreach (var hit in found)
                        {
                            try { ((dynamic)hit).Delete(); }
                            catch (COMException ex)
                            {
                                WriteLine($"LIVE-RESULT: cleanup-error: could not purge from Deleted Items: {ex.Message}");
                                ok = false;
                            }
                        }
                    }
                    finally
                    {
                        foreach (var hit in found)
                            if (Marshal.IsComObject(hit)) Marshal.ReleaseComObject(hit);
                        if (restricted is not null) Marshal.ReleaseComObject(restricted);
                        if (items is not null) Marshal.ReleaseComObject(items);
                        if (folder is not null) Marshal.ReleaseComObject(folder);
                    }
                }
            }
            finally
            {
                foreach (var store in targets) Marshal.ReleaseComObject(store);
            }
        }
        catch (COMException ex)
        {
            WriteLine($"LIVE-RESULT: cleanup-error: Deleted Items purge failed: {ex.Message}");
            ok = false;
        }
        finally
        {
            if (session is not null) Marshal.ReleaseComObject(session);
        }
        return ok;
    }

    public void Result(string key, string value)
    {
        var line = $"LIVE-RESULT: {key}: {value}";
        WriteLine(line);
        TestContext.Current?.TestOutputHelper?.WriteLine(line);
    }

    /// <summary>Appends "LIVE-LEFTOVER: {text}" ("mailbox|folder|subject") to the results file.</summary>
    public void Leftover(string text) => WriteLine("LIVE-LEFTOVER: " + text);

    private void WriteLine(string line)
    {
        try
        {
            lock (_resultsLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
                File.AppendAllText(ResultsPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The results file is a convenience; the tests must not fail because of it.
        }
    }

    public async Task<LiveSyncOutcome> EnsureSyncedAsync()
    {
        await _syncGate.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        try
        {
            if (_sync is null && _syncSkip is null)
            {
                try
                {
                    _sync = await RunSyncAsync().ConfigureAwait(false);
                }
                catch (MailSourceUnavailableException ex)
                {
                    _syncSkip = ex.InnerException is OutlookNotRespondingException
                        ? SkipTexts.NotResponding("the sync", 60)
                        : SkipTexts.NotRunning;
                }
                catch (OutlookNotRespondingException)
                {
                    _syncSkip = SkipTexts.NotResponding("the sync", 60);
                }
                catch (OutlookUnavailableException)
                {
                    _syncSkip = SkipTexts.NotRunning;
                }
            }
        }
        finally
        {
            _syncGate.Release();
        }
        if (_syncSkip is not null) Assert.Skip(_syncSkip);
        return _sync!;
    }

    private async Task<LiveSyncOutcome> RunSyncAsync()
    {
        var engine = new SyncEngine(new OutlookMailSource(Worker, Stores, Mail), Hub, logger: null, reconcile: null);
        var started = DateTimeOffset.UtcNow;
        var cutoff = started.AddMonths(-1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        MailboxSyncResult? result = null;
        var timedOut = false;
        try
        {
            result = await engine.SyncMailboxAsync(new MailboxPlan("", ["Inbox"], 1), Indexes.For(""), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            timedOut = true;
        }
        var status = Indexes.For("").Status();
        var folder = result?.Folders.FirstOrDefault();
        var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
        Result("sync", $"{status.TotalEmails} emails in {seconds:0} s, backfill complete={folder?.BackfillComplete ?? false}, timed out={timedOut}");
        return new LiveSyncOutcome(result, timedOut, status.TotalEmails, started, cutoff);
    }

    // ---- COM helpers (worker thread only) ----

    /// <summary>Runs <paramref name="f"/> on a default folder of the store of <paramref name="mailbox"/>; releases everything.</summary>
    internal T OnFolder<T>(Outlook.Application app, string mailbox, Outlook.OlDefaultFolders which, Func<Outlook.MAPIFolder, T> f)
    {
        Outlook.Store? store = null;
        Outlook.MAPIFolder? folder = null;
        try
        {
            store = Stores.Get(app, mailbox);
            folder = store.GetDefaultFolder(which);
            return f(folder);
        }
        finally
        {
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    internal string StoreIdOf(Outlook.Application app, string mailbox)
    {
        var store = Stores.Get(app, mailbox);
        try { return store.StoreID; }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal string DefaultFolderId(Outlook.Application app, string mailbox, Outlook.OlDefaultFolders which) =>
        OnFolder(app, mailbox, which, f => f.EntryID);

    /// <summary>Subjects in the folder that contain the text (DASL subject LIKE). The text must be [a-z0-9-] only.</summary>
    internal static List<string> SubjectsContaining(Outlook.MAPIFolder folder, string text)
    {
        var result = new List<string>();
        Outlook.Items? items = null, restricted = null;
        try
        {
            items = folder.Items;
            restricted = items.Restrict($"@SQL=\"urn:schemas:httpmail:subject\" LIKE '%{text}%'");
            object? item = restricted.GetFirst();
            while (item is not null)
            {
                try
                {
                    dynamic d = item;
                    result.Add((string?)d.Subject ?? "");
                }
                finally
                {
                    if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
                item = restricted.GetNext();
            }
        }
        finally
        {
            if (restricted is not null) Marshal.ReleaseComObject(restricted);
            if (items is not null) Marshal.ReleaseComObject(items);
        }
        return result;
    }

    internal List<string> TokenSubjectsIn(Outlook.Application app, string mailbox, Outlook.OlDefaultFolders which) =>
        OnFolder(app, mailbox, which, f => SubjectsContaining(f, RunToken));

    public async Task<SentSnapshot> SnapshotSentAsync(string? mailbox = null)
    {
        var key = EmailAddress.Normalize(mailbox);
        return await RunAsync("the sent-items snapshot", app => new SentSnapshot(key,
            OnFolder(app, key, Outlook.OlDefaultFolders.olFolderSentMail, CountOf),
            OnFolder(app, key, Outlook.OlDefaultFolders.olFolderOutbox, CountOf))).ConfigureAwait(false);
    }

    private static int CountOf(Outlook.MAPIFolder folder)
    {
        var items = folder.Items;
        try { return items.Count; }
        finally { Marshal.ReleaseComObject(items); }
    }

    /// <summary>The signed-in user. Skips on NotResponding/Unavailable. Tests only ever address this person.</summary>
    internal async Task<Recipient> MeAsync()
    {
        try
        {
            return await new OutlookFreeBusySource(Worker).GetCurrentUserAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OutlookNotRespondingException)
        {
            Assert.Skip(SkipTexts.NotResponding("looking up the current user", 30));
            throw;
        }
        catch (OutlookUnavailableException)
        {
            Assert.Skip(SkipTexts.NotRunning);
            throw;
        }
    }

    /// <summary>The items a test created. Everything tracked here is also in the run ledger.</summary>
    internal sealed class Scope(LiveOutlook owner)
    {
        internal List<TrackedItem> Items { get; } = [];

        public TrackedItem Track(TrackedItem item)
        {
            Items.Add(item);
            owner.Track(item);
            return item;
        }
    }

    /// <summary>
    /// The pattern every creating test uses: snapshot Sent Items and Outbox, run the body, then in a finally
    /// delete exactly the tracked items, check that no token item is left in <paramref name="leftIn"/> of the
    /// mailboxes, and assert nothing was sent. A cleanup problem fails the test unless the body already failed.
    /// </summary>
    internal async Task WithCleanupAsync(string[] mailboxes, Outlook.OlDefaultFolders leftIn, Func<Scope, Task> body)
    {
        var befores = new List<SentSnapshot>();
        foreach (var m in mailboxes) befores.Add(await SnapshotSentAsync(m).ConfigureAwait(false));
        var scope = new Scope(this);
        Exception? failure = null;
        try
        {
            await body(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var problem = "";
            try
            {
                foreach (var item in scope.Items.ToList())
                    if (!await DeleteAsync(item).ConfigureAwait(false))
                        problem += $"Could not delete \"{item.Subject}\". ";
                foreach (var m in mailboxes)
                {
                    var key = m;
                    var left = await Worker.RunAsync(app => TokenSubjectsIn(app, key, leftIn), WorkPriority.Interactive,
                        CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    if (left.Count > 0) problem += $"{left.Count} test item(s) are still in {leftIn} of '{key}'. ";
                }
                foreach (var b in befores) await AssertNothingSentAsync(b).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                problem += ex.Message;
            }
            if (problem.Length > 0)
            {
                if (failure is null) Assert.Fail(problem);
                else Result("cleanup-problem", problem);
            }
        }
    }

    public async Task AssertNothingSentAsync(SentSnapshot before)
    {
        var now = await SnapshotSentAsync(before.Mailbox).ConfigureAwait(false);
        var tokens = await RunAsync("the sent-items check", app =>
            (TokenSubjectsIn(app, before.Mailbox, Outlook.OlDefaultFolders.olFolderSentMail),
             TokenSubjectsIn(app, before.Mailbox, Outlook.OlDefaultFolders.olFolderOutbox))).ConfigureAwait(false);
        Assert.True(tokens.Item1.Count == 0, "A test item reached Sent Items: " + string.Join("; ", tokens.Item1));
        Assert.True(tokens.Item2.Count == 0, "A test item reached the Outbox: " + string.Join("; ", tokens.Item2));
        Assert.True(now.SentItems == before.SentItems && now.Outbox == before.Outbox,
            $"Sent Items/Outbox counts changed during the test: Sent {before.SentItems}->{now.SentItems}, Outbox {before.Outbox}->{now.Outbox}. "
            + "If you sent mail yourself meanwhile, ignore this.");
    }
}

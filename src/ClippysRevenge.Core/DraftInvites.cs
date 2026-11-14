using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>One tracked meeting, as stored in draft-invites.json (SPEC 22.3). Mailbox: "" = own calendar,
/// else the normalized shared-mailbox SMTP.</summary>
public sealed record DraftInviteEntry(
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("storeId")] string StoreId,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);

/// <summary>Read from Outlook (or known at creation); never stored on disk. Start/End are local, Kind Unspecified.</summary>
public sealed record DraftInviteDetails(string Subject, DateTime Start, DateTime End);

/// <summary>A tracked meeting for the tray list. Details is null until it was created in this run or read by a check.</summary>
public sealed record PendingDraftInvite(DraftInviteEntry Entry, DraftInviteDetails? Details);

public enum DraftInviteLookup { Found, NotFound, Unreadable }

/// <summary>What the COM side saw for one entry. For NotFound/Unreadable only Lookup is meaningful.</summary>
public sealed record DraftInviteSnapshot(
    DraftInviteLookup Lookup,
    bool InDeletedItems = false,
    int MeetingStatus = 0,               // OlMeetingStatus: 0 non-meeting, 1 meeting, 3 received, 5 cancelled, 7 received+cancelled
    bool? Invited = null,                // PidLidFInvited; null = property missing or unreadable
    string? Categories = null,
    DraftInviteDetails? Details = null,
    bool OpenInInspector = false);

public enum DraftInviteAction
{
    Keep,            // stays tracked, no write
    Drop,            // stops being tracked, no write
    RemoveCategory,  // write NewCategories + Save(), then drop (kept if the write fails)
}

public enum DraftInviteReason { Unsent, Sent, NotAMeeting, Cancelled, NotFound, Deleted, Expired, CategoryGone, OpenInInspector, Unreadable }

public sealed record DraftInviteDecision(DraftInviteAction Action, DraftInviteReason Reason, string? NewCategories = null);

/// <summary>Applied: true when Action is RemoveCategory and the Categories write + Save() succeeded;
/// false otherwise (also for Keep/Drop, where it is ignored).</summary>
public sealed record DraftInviteOutcome(DraftInviteEntry Entry, DraftInviteDecision Decision,
    DraftInviteDetails? Details, bool Applied);

/// <summary>COM seam for the check (App: OutlookDraftInviteCalendar).</summary>
public interface IDraftInviteCalendar
{
    /// <summary>
    /// One COM work item at <paramref name="priority"/>. For each entry, in order: look the item up, build a snapshot,
    /// call <paramref name="decide"/>, and for RemoveCategory set Categories to NewCategories and call Save() (nothing
    /// else). Returns one outcome per entry, same order. Releases every COM object.
    /// </summary>
    /// <exception cref="OutlookUnavailableException">Outlook is gone or not responding (also the subclass).</exception>
    /// <exception cref="OperationCanceledException">ct fired before the work item ran.</exception>
    Task<IReadOnlyList<DraftInviteOutcome>> CheckAsync(IReadOnlyList<DraftInviteEntry> entries,
        Func<DraftInviteSnapshot, DraftInviteDecision> decide, WorkPriority priority, CancellationToken ct);
}

/// <summary>What create_meeting_draft needs (MeetingInvites). Never throws.</summary>
public interface IDraftInviteTracker
{
    void Track(DraftInviteEntry entry, DraftInviteDetails? details);
}

/// <summary>Edits of an Outlook Categories string (SPEC 22.2). Pure.</summary>
public static class DraftInviteCategories
{
    private static string[] Separators(string? listSeparator)
    {
        var seps = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(listSeparator)) seps.Add(listSeparator.Trim());
        seps.Add(",");
        seps.Add(";");
        return [.. seps];
    }

    /// <summary>Splits on listSeparator (trimmed; ignored when null/blank), ',' and ';'; entries trimmed; empty entries dropped.</summary>
    public static IReadOnlyList<string> Split(string? categories, string? listSeparator)
    {
        if (string.IsNullOrWhiteSpace(categories)) return [];
        return categories.Split(Separators(listSeparator), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Removes every entry equal to <paramref name="name"/> (OrdinalIgnoreCase), keeping the others in order.
    /// False (result = categories ?? "") when no entry matched: the caller writes nothing.
    /// The kept entries are joined with the first separator that occurs in the original string followed by one
    /// space (", " for "A, B"; "; " for "A;B"); a single kept entry has no separator; none kept gives "".
    /// </summary>
    public static bool TryRemove(string? categories, string name, string? listSeparator, out string result)
    {
        result = categories ?? "";
        var target = (name ?? "").Trim();
        var parts = Split(categories, listSeparator);
        if (!parts.Any(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase))) return false;

        var kept = parts.Where(p => !string.Equals(p, target, StringComparison.OrdinalIgnoreCase)).ToList();
        var joiner = ", ";
        var best = int.MaxValue;
        foreach (var sep in Separators(listSeparator))
        {
            var i = result.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0 && i < best)
            {
                best = i;
                joiner = sep + " ";
            }
        }
        result = string.Join(joiner, kept);
        return true;
    }
}

/// <summary>%APPDATA%\ClippysRevenge\draft-invites.json (SPEC 22.3). Not thread-safe; DraftInvites serializes access.</summary>
public sealed class DraftInvitesStore
{
    public const string FileName = "draft-invites.json";
    public const int MaxEntries = 200;
    public const int MaxFileBytes = 1_048_576;
    public const int MaxEntryIdLength = 1024;
    public const int MaxStoreIdLength = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger? _log;

    /// <exception cref="ArgumentException">path is null or blank.</exception>
    public DraftInvitesStore(string path, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _log = log;
    }

    public string Path { get; }

    /// <summary>Never throws. Missing file: empty, no log. Unreadable, too large, not a JSON array or invalid JSON:
    /// empty and one Warning (type only); the file is left as it is and replaced by the next Save.
    /// Invalid entries are skipped one by one; duplicates (EntryId, OrdinalIgnoreCase) keep the first;
    /// more than MaxEntries keeps the newest by CreatedUtc. Result sorted by CreatedUtc ascending.</summary>
    public IReadOnlyList<DraftInviteEntry> Load()
    {
        string failure;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists) return [];
            if (info.Length > MaxFileBytes)
            {
                failure = "TooLarge";
            }
            else
            {
                var bytes = ReadShared(Path);
                if (bytes.Length > MaxFileBytes)
                {
                    failure = "TooLarge";
                }
                else
                {
                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        failure = "NotAnArray";
                    }
                    else
                    {
                        var read = new List<DraftInviteEntry>();
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            try
                            {
                                var e = JsonSerializer.Deserialize<DraftInviteEntry>(el.GetRawText(), JsonOptions);
                                if (e is not null) read.Add(e);
                            }
                            catch (JsonException)
                            {
                                // Invalid entry: skipped.
                            }
                        }
                        return Sanitize(read);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
        }
        _log?.LogWarning("draft-invites.json could not be read ({Type}); starting with an empty list.", failure);
        return [];
    }

    /// <summary>Atomic (Path + ".tmp", then File.Move overwrite). Creates the folder. Writes at most MaxEntries
    /// (newest by CreatedUtc), sorted by CreatedUtc ascending, indented JSON array.</summary>
    /// <exception cref="IOException">and UnauthorizedAccessException, unchanged.</exception>
    public void Save(IReadOnlyList<DraftInviteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var clean = Sanitize(entries);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(clean, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static List<DraftInviteEntry> Sanitize(IEnumerable<DraftInviteEntry?> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DraftInviteEntry>();
        foreach (var e in entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.EntryId) || e.CreatedUtc == default) continue;
            var id = e.EntryId.Trim();
            var store = (e.StoreId ?? "").Trim();
            if (id.Length > MaxEntryIdLength || store.Length > MaxStoreIdLength) continue;
            if (!seen.Add(id)) continue;
            list.Add(new DraftInviteEntry(id, store, MailboxIndexes.Normalize(e.Mailbox), e.CreatedUtc));
        }
        if (list.Count > MaxEntries)
        {
            list = [.. list.OrderByDescending(e => e.CreatedUtc).Take(MaxEntries)];
        }
        return [.. list.OrderBy(e => e.CreatedUtc)];
    }
}

/// <summary>Tracked draft invites: list, check, triggers (SPEC 22.2, 22.3). Thread-safe.</summary>
public sealed class DraftInvites : IDraftInviteTracker
{
    public const string CategoryName = "Clippy draft invite";
    public const string FInvitedProperty =
        "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/8229000B";
    public const int MaxPerCheck = 50;
    public static readonly TimeSpan MinBackgroundInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExpireAfterEnd = TimeSpan.FromDays(30);

    private const int OlMeeting = 1;
    private const int OlMeetingCanceled = 5;
    private const int OlMeetingReceivedAndCanceled = 7;

    private readonly IDraftInviteCalendar _calendar;
    private readonly DraftInvitesStore _store;
    private readonly Func<bool> _available;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly string _sep;

    private readonly Lock _lock = new();
    private readonly List<DraftInviteEntry> _entries;
    private readonly Dictionary<string, DraftInviteDetails> _details = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastChecked = new(StringComparer.OrdinalIgnoreCase);
    private Task? _inFlight;
    private DateTimeOffset? _lastStart;
    private DateTimeOffset? _lastCheckUtc;

    /// <param name="outlookAvailable">True only while the COM worker is attached (App: worker.State == Available).</param>
    /// <param name="listSeparator">Null = CultureInfo.CurrentCulture.TextInfo.ListSeparator.</param>
    /// <remarks>Loads the store once, here.</remarks>
    public DraftInvites(IDraftInviteCalendar calendar, DraftInvitesStore store, Func<bool> outlookAvailable,
        TimeZoneInfo timeZone, TimeProvider? time = null, ILogger? logger = null, string? listSeparator = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(outlookAvailable);
        ArgumentNullException.ThrowIfNull(timeZone);
        _calendar = calendar;
        _store = store;
        _available = outlookAvailable;
        _zone = timeZone;
        _time = time ?? TimeProvider.System;
        _log = logger;
        _sep = listSeparator ?? CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        _entries = [.. store.Load()];
    }

    /// <summary>Number of tracked entries (the badge count, SPEC 22.6). Unchanged while Outlook is unavailable.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Snapshot copy: entries with known Details sorted by Details.Start, then entries without Details by CreatedUtc.</summary>
    public IReadOnlyList<PendingDraftInvite> Pending
    {
        get { lock (_lock) return PendingLocked(); }
    }

    /// <summary>When the last check finished with a result from Outlook; null before the first one.</summary>
    public DateTimeOffset? LastCheckUtc
    {
        get { lock (_lock) return _lastCheckUtc; }
    }

    /// <summary>Raised after the tracked list or any entry's Details changed (Track, Forget, a check). Any thread;
    /// each handler called separately, exceptions caught and logged (Warning, type only). Not raised when nothing changed.</summary>
    public event Action? Changed;

    /// <summary>Adds or refreshes an entry (no COM). Ignored when EntryId is blank (Warning). Never throws.</summary>
    public void Track(DraftInviteEntry entry, DraftInviteDetails? details)
    {
        try
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.EntryId))
            {
                _log?.LogWarning("A created meeting draft has no entry id; it is not tracked.");
                return;
            }
            var id = entry.EntryId.Trim();
            var clean = new DraftInviteEntry(id, (entry.StoreId ?? "").Trim(), MailboxIndexes.Normalize(entry.Mailbox), entry.CreatedUtc);
            lock (_lock)
            {
                var idx = _entries.FindIndex(e => string.Equals(e.EntryId, id, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    if (details is not null) _details[_entries[idx].EntryId] = details;
                }
                else
                {
                    _entries.Add(clean);
                    if (details is not null) _details[id] = details;
                    while (_entries.Count > DraftInvitesStore.MaxEntries)
                    {
                        var oldest = _entries.MinBy(e => e.CreatedUtc)!;
                        _entries.Remove(oldest);
                        _details.Remove(oldest.EntryId);
                        _lastChecked.Remove(oldest.EntryId);
                    }
                }
                PersistLocked();
            }
            RaiseChanged();
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Tracking a draft invite failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Stops tracking (M15.4: the tray line's meeting is gone). False when not tracked. No COM. Never throws.</summary>
    public bool Forget(string entryId)
    {
        try
        {
            lock (_lock)
            {
                var idx = _entries.FindIndex(e => string.Equals(e.EntryId, entryId?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return false;
                var id = _entries[idx].EntryId;
                _entries.RemoveAt(idx);
                _details.Remove(id);
                _lastChecked.Remove(id);
                PersistLocked();
            }
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Forgetting a draft invite failed ({Type}).", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Runs a check (or joins the one already running) and returns Pending afterwards. Returns Pending without COM when
    /// nothing is tracked or outlookAvailable() is false. Never throws except OperationCanceledException when ct fires
    /// (ct only cancels this caller's wait, never the shared check).
    /// </summary>
    public async Task<IReadOnlyList<PendingDraftInvite>> CheckAsync(WorkPriority priority, CancellationToken ct = default)
    {
        Task wait;
        lock (_lock)
        {
            if (_entries.Count == 0 || !IsAvailable()) return PendingLocked();
            if (_inFlight is null)
            {
                _lastStart = _time.GetUtcNow();
                _inFlight = Task.Run(() => RunCheckAsync(priority));
            }
            wait = _inFlight;
        }
        await wait.WaitAsync(ct).ConfigureAwait(false);
        return Pending;
    }

    /// <summary>SyncService.CycleCompleted hook. Starts a Background check when something is tracked, Outlook is
    /// available, no check is running and the last check started at least MinBackgroundInterval ago (or never).
    /// Returns the started task (for tests) or null. Never blocks, never throws.</summary>
    public Task? OnSyncCycleCompleted()
    {
        try
        {
            lock (_lock)
            {
                if (_entries.Count == 0 || _inFlight is not null || !IsAvailable()) return null;
                if (_lastStart is { } last && _time.GetUtcNow() - last < MinBackgroundInterval) return null;
                return CheckAsync(WorkPriority.Background);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Starting the draft invite check failed ({Type}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The per-entry rule (SPEC 22.3). Pure.</summary>
    public static DraftInviteDecision Decide(DraftInviteSnapshot s, DateTime nowLocal, string? listSeparator)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Lookup == DraftInviteLookup.NotFound)
            return new(DraftInviteAction.Drop, DraftInviteReason.NotFound);
        if (s.Lookup == DraftInviteLookup.Unreadable)
            return new(DraftInviteAction.Keep, DraftInviteReason.Unreadable);
        if (s.InDeletedItems)
            return new(DraftInviteAction.Drop, DraftInviteReason.Deleted);

        if (s.Invited == true || s.MeetingStatus != OlMeeting)
        {
            var reason = s.Invited == true ? DraftInviteReason.Sent
                : s.MeetingStatus is OlMeetingCanceled or OlMeetingReceivedAndCanceled ? DraftInviteReason.Cancelled
                : DraftInviteReason.NotAMeeting;
            if (!DraftInviteCategories.TryRemove(s.Categories, CategoryName, listSeparator, out var next))
                return new(DraftInviteAction.Drop, DraftInviteReason.CategoryGone);
            if (s.OpenInInspector)
                return new(DraftInviteAction.Keep, DraftInviteReason.OpenInInspector);
            return new(DraftInviteAction.RemoveCategory, reason, next);
        }

        if (s.Details is not null && s.Details.End < nowLocal - ExpireAfterEnd)
            return new(DraftInviteAction.Drop, DraftInviteReason.Expired);
        return new(DraftInviteAction.Keep, DraftInviteReason.Unsent);
    }

    private bool IsAvailable()
    {
        try
        {
            return _available();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Called under the lock.
    private List<PendingDraftInvite> PendingLocked()
    {
        var withDetails = new List<PendingDraftInvite>();
        var without = new List<PendingDraftInvite>();
        foreach (var e in _entries)
        {
            if (_details.TryGetValue(e.EntryId, out var d)) withDetails.Add(new PendingDraftInvite(e, d));
            else without.Add(new PendingDraftInvite(e, null));
        }
        return [.. withDetails.OrderBy(p => p.Details!.Start), .. without.OrderBy(p => p.Entry.CreatedUtc)];
    }

    // Called under the lock.
    private void PersistLocked()
    {
        try
        {
            _store.Save(_entries);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("draft-invites.json could not be saved ({Type}).", ex.GetType().Name);
        }
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                _log?.LogWarning("A draft invites Changed handler failed ({Type}).", ex.GetType().Name);
            }
        }
    }

    private async Task RunCheckAsync(WorkPriority priority)
    {
        try
        {
            DraftInviteEntry[] batch;
            lock (_lock)
            {
                batch = [.. _entries
                    .OrderBy(e => _lastChecked.TryGetValue(e.EntryId, out var t) ? t : DateTimeOffset.MinValue)
                    .ThenBy(e => e.CreatedUtc)
                    .Take(MaxPerCheck)];
            }
            if (batch.Length == 0) return;

            var nowLocal = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime;
            IReadOnlyList<DraftInviteOutcome> outcomes;
            try
            {
                outcomes = await _calendar.CheckAsync(batch, s => Decide(s, nowLocal, _sep), priority, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OutlookUnavailableException)
            {
                _log?.LogInformation("Draft invite check stopped: Outlook is not available.");
                return;
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Draft invite check failed ({Type}).", ex.GetType().Name);
                return;
            }

            var changed = false;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                var removed = false;
                foreach (var o in outcomes)
                {
                    var idx = _entries.FindIndex(e => string.Equals(e.EntryId, o.Entry.EntryId, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) continue;
                    var id = _entries[idx].EntryId;
                    var applied = o.Decision.Action == DraftInviteAction.RemoveCategory && o.Applied;
                    if (o.Decision.Action == DraftInviteAction.Drop || applied)
                    {
                        _log?.LogInformation("Draft invite no longer tracked ({Reason}).", o.Decision.Reason);
                        if (applied)
                            _log?.LogInformation("Draft invite sent; Clippy's category was removed ({Reason}).", o.Decision.Reason);
                        _entries.RemoveAt(idx);
                        _details.Remove(id);
                        _lastChecked.Remove(id);
                        removed = true;
                        continue;
                    }
                    _lastChecked[id] = now;
                    if (o.Details is not null && (!_details.TryGetValue(id, out var old) || old != o.Details))
                    {
                        _details[id] = o.Details;
                        changed = true;
                    }
                }
                if (removed)
                {
                    PersistLocked();
                    changed = true;
                }
                _lastCheckUtc = now;
            }
            if (changed) RaiseChanged();
        }
        finally
        {
            lock (_lock) _inFlight = null;
        }
    }
}

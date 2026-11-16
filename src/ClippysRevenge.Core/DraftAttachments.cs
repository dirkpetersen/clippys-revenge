using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public enum AttachmentKind { File, Email, Cloud, Link }      // prompt kind="file|email|cloud|link"
public enum AttachmentOrigin { Email, Thread }               // prompt source="email|thread"

/// <param name="Mailbox">The mailbox key as in DraftPromptInput.MailboxKey: "" = own, else the shared
/// mailbox's normalized SMTP. Passed through to IAttachmentSource.ReadAsync.</param>
/// <param name="AnchorBody">Plain-text body of the email being answered (index body; manual path: COM-read).</param>
/// <param name="AnchorAttachments">The anchor's attachments (index or live read), inline ones included.</param>
/// <param name="Thread">EmailIndex.ThreadAttachments rows for the anchor (the anchor excluded), newest first.</param>
/// <param name="SignatureText">Plain text of the user's reply signature, for MailText.OwnText, or null.</param>
/// <param name="Zone">Local zone for the attached-item Date line.</param>
public sealed record AttachmentRequest(
    string Mailbox,
    string AnchorEntryId,
    string? AnchorSubject,
    string? AnchorBody,
    string? AnchorSenderName,
    string? AnchorSenderEmail,
    DateTimeOffset AnchorTime,
    IReadOnlyList<AttachmentInfo> AnchorAttachments,
    IReadOnlyList<ThreadAttachmentSource> Thread,
    AiIdentity Identity,
    string? SignatureText,
    WorkPriority Priority,
    TimeZoneInfo Zone);

/// <summary>SPEC 26.8 seam; the App implements it in OutlookAttachmentSource (M19.7).</summary>
public interface IAttachmentSource
{
    /// <summary>One COM work item at <paramref name="priority"/>: opens the email, matches <paramref name="wanted"/>
    /// (AttachmentSelection.MatchLive), and returns its content. Null when the email can't be opened.</summary>
    /// <exception cref="OutlookUnavailableException"/>
    Task<AiAttachmentRead?> ReadAsync(string mailbox, string entryId, AttachmentInfo wanted, long maxBytes,
        WorkPriority priority, CancellationToken ct);

    /// <summary>The synced OneDrive scopes, read fresh (registry; no COM). Never throws; empty on error.</summary>
    IReadOnlyList<SyncedRoot> SyncedRoots();
}

public abstract record AiAttachmentRead          // closed hierarchy (private ctor)
{
    private AiAttachmentRead() { }
    public sealed record File(byte[] Bytes) : AiAttachmentRead;
    public sealed record Cloud(string? Url, string DisplayName) : AiAttachmentRead;
    public sealed record Item(string? Subject, string? From, DateTimeOffset? Sent, string? Body,
        IReadOnlyList<string> AttachmentNames) : AiAttachmentRead;
    public sealed record TooLarge(long Bytes) : AiAttachmentRead;
    public sealed record NotOnEmail : AiAttachmentRead;
    public sealed record Failed(string ErrorType) : AiAttachmentRead;
}

/// <summary>One &lt;attachment&gt; of the prompt (rendered by M19.5). Name is the full display name (M19.5 cuts
/// it to 200). Text is "" unless Status is Read or Truncated. Reason is one AttachmentReasons text, set only
/// when Skipped. SenderName/SenderEmail/MessageTime describe the message that carries it (M19.5 uses them only
/// for Origin Thread).</summary>
/// <param name="EntryId">EntryID of the message that carries the item (PlannedAttachment.EntryId).</param>
/// <param name="FileName">The index file name of the attachment (PlannedAttachment.Attachment.FileName); null for
/// links found in a body. Used only for the " (link)" suffix (SPEC 26.6).</param>
public sealed record PromptAttachment(string Name, AttachmentOrigin Origin, AttachmentKind Kind,
    ExtractStatus Status, string Text, string? Reason, int Tier,
    string? SenderName, string? SenderEmail, DateTimeOffset MessageTime,
    string? EntryId = null, string? FileName = null);

/// <summary>The SPEC 26.7 table rows. Texts are M19.5 (AttachmentNoteTexts).</summary>
public enum AttachmentNoteKind
{
    NotSynced, FromOutside, EarlierEmailGone, PasswordProtected, TooLarge, NoText,
    Format, Damaged, TookTooLong, TooMuch, Truncated,
}

/// <summary>Shared = the item is a link or cloud attachment ("the shared file" wording).
/// Extension: lower-case, only for Format.</summary>
public sealed record AttachmentNote(AttachmentNoteKind Kind, string Name, bool Shared, string? Extension = null);

/// <summary>Read/Truncated/Skipped: items by final status. Links: items of kind Link or Cloud.
/// Chars: sum of final Text lengths. ElapsedMs: wall clock of CollectAsync.</summary>
public sealed record AttachmentCounts(int Read, int Truncated, int Skipped, int Links, int Chars, long ElapsedMs)
{
    public static readonly AttachmentCounts None = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Items in tier order. Notes: every note (tiers 1-2), in item order, NOT limited to 5 (M19.5 limits).</summary>
public sealed record AttachmentSet(IReadOnlyList<PromptAttachment> Items, IReadOnlyList<AttachmentNote> Notes,
    AttachmentCounts Counts)
{
    public static readonly AttachmentSet Empty = new([], [], AttachmentCounts.None);
}

/// <summary>
/// SPEC 26.8 "Collect": plans, reads (one item at a time), extracts and budgets the attachment text of one
/// prompt. Nothing is stored, cached or logged except counts and exception type names.
/// </summary>
public sealed class DraftAttachments
{
    public const int MaxItems = 20;
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(10);

    private readonly IAttachmentSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, ExtractedText> _extract;
    private readonly Func<MappedLink.Local, long, CancellationToken, SyncedFileResult> _readSynced;

    /// <param name="extract">Test seam; default AttachmentText.Extract.</param>
    /// <param name="readSynced">Test seam; default SyncedFileReader.Read.</param>
    /// <exception cref="ArgumentNullException">source is null.</exception>
    public DraftAttachments(IAttachmentSource source, TimeProvider? time = null, ILogger? logger = null,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ExtractedText>? extract = null,
        Func<MappedLink.Local, long, CancellationToken, SyncedFileResult>? readSynced = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _extract = extract ?? AttachmentText.Extract;
        _readSynced = readSynced ?? SyncedFileReader.Read;
    }

    /// <summary>The result of one read-and-extract run. ErrorType: exception type name for the Warning, or null.</summary>
    private sealed record Outcome(ExtractStatus Status, string Text, string? Reason, string? ErrorType = null)
    {
        public static Outcome Skip(string reason, string? errorType = null) =>
            new(ExtractStatus.Skipped, "", reason, errorType);

        public static Outcome From(ExtractedText e) => new(e.Status, e.Text, e.Reason);
    }

    /// <summary>One item of the walk. Dropped items never become a Final.</summary>
    private sealed record Final(PlannedAttachment Plan, string Name, AttachmentKind Kind, ExtractStatus Status,
        string Text, string? Reason)
    {
        public static Final Skipped(PlannedAttachment p, string name, AttachmentKind kind, string reason) =>
            new(p, name, kind, ExtractStatus.Skipped, "", reason);
    }

    /// <summary>SPEC 26.8 "Collect". Throws only ArgumentNullException (request) and OperationCanceledException
    /// (ct cancelled). OutlookUnavailableException from the source is handled (remaining items skipped).</summary>
    public async Task<AttachmentSet> CollectAsync(AttachmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var start = _time.GetTimestamp();

        IReadOnlyList<SyncedRoot> roots;
        try
        {
            roots = _source.SyncedRoots() ?? [];
        }
        catch (Exception e)
        {
            Warn(e.GetType().Name);
            roots = [];
        }

        var plan = DraftAttachmentPlan.Build(request, roots);
        if (plan.Count == 0)
            return AttachmentSet.Empty with { Counts = AttachmentCounts.None with { ElapsedMs = Elapsed(start) } };

        var finals = new List<Final>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var goneEntries = new HashSet<string>(StringComparer.Ordinal);
        var outlookGone = false;

        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();

            if (p.SkipReason is not null)
            {
                finals.Add(Final.Skipped(p, p.Name, p.Kind, p.SkipReason));
                continue;
            }

            if (p.Link is { } link)
            {
                var local = p.Mapped as MappedLink.Local;
                if (local is not null
                    && (seenUrls.Contains(link.NormalizedUrl) || seenPaths.Contains(local.Path))) continue;
                seenUrls.Add(link.NormalizedUrl);
                if (local is not null) seenPaths.Add(local.Path);

                if (outlookGone) finals.Add(Final.Skipped(p, p.Name, AttachmentKind.Link, AttachmentReasons.OutlookNotRunning));
                else if (OutOfTime(start)) finals.Add(Final.Skipped(p, p.Name, AttachmentKind.Link, AttachmentReasons.OutOfTime));
                else if (local is null) finals.Add(Final.Skipped(p, p.Name, AttachmentKind.Link, AttachmentReasons.NotSynced));
                else finals.Add(FromOutcome(p, p.Name, AttachmentKind.Link, await ReadLinkAsync(local, ct)));
                continue;
            }

            // An attachment of a message.
            var att = p.Attachment!;
            var name = p.Name;
            if (outlookGone)
            {
                finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.OutlookNotRunning));
                continue;
            }
            if (OutOfTime(start))
            {
                finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.OutOfTime));
                continue;
            }
            if (goneEntries.Contains(p.EntryId))
            {
                finals.Add(Final.Skipped(p, name, AttachmentKind.File, GoneReason(p)));
                continue;
            }

            AiAttachmentRead? r;
            try
            {
                r = await _source.ReadAsync(request.Mailbox, p.EntryId, att, AttachmentText.MaxFileBytes,
                    request.Priority, ct);
            }
            catch (OutlookUnavailableException)
            {
                outlookGone = true;
                finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.OutlookNotRunning));
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Warn(e.GetType().Name);
                finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.Damaged));
                continue;
            }
            ct.ThrowIfCancellationRequested();

            switch (r)
            {
                case null:
                    goneEntries.Add(p.EntryId);
                    finals.Add(Final.Skipped(p, name, AttachmentKind.File, GoneReason(p)));
                    break;

                case AiAttachmentRead.File f:
                    if (f.Bytes.Length > AttachmentText.MaxFileBytes)
                        finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.TooLarge));
                    else
                    {
                        var bytes = f.Bytes;
                        finals.Add(FromOutcome(p, name, AttachmentKind.File,
                            await RunAsync(token => Outcome.From(_extract(att.FileName, bytes, token)), ct)));
                    }
                    break;

                case AiAttachmentRead.Item item:
                    finals.Add(FromOutcome(p, name, AttachmentKind.Email, RenderItem(item, request.Zone)));
                    break;

                case AiAttachmentRead.Cloud cloud:
                    var cloudFinal = await ReadCloudAsync(p, cloud, roots, seenUrls, seenPaths, ct);
                    if (cloudFinal is not null) finals.Add(cloudFinal);
                    break;

                case AiAttachmentRead.TooLarge:
                    finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.TooLarge));
                    break;

                case AiAttachmentRead.NotOnEmail:
                    finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.NotOnEmail));
                    break;

                case AiAttachmentRead.Failed failed:
                    Warn(failed.ErrorType);
                    finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.Damaged));
                    break;

                default:
                    finals.Add(Final.Skipped(p, name, AttachmentKind.File, AttachmentReasons.Damaged));
                    break;
            }
        }

        return Finish(finals, start);
    }

    private static string GoneReason(PlannedAttachment p) =>
        p.Origin == AttachmentOrigin.Thread ? AttachmentReasons.EarlierEmailGone : AttachmentReasons.NotOnEmail;

    private bool OutOfTime(long start) => _time.GetElapsedTime(start) >= TimeBudget;

    private long Elapsed(long start) => (long)_time.GetElapsedTime(start).TotalMilliseconds;

    private void Warn(string errorType) =>
        _logger?.LogWarning("AI drafts: an attachment couldn't be read ({Type})", errorType);

    private Final FromOutcome(PlannedAttachment p, string name, AttachmentKind kind, Outcome o)
    {
        if (o.ErrorType is not null) Warn(o.ErrorType);
        return new Final(p, name, kind, o.Status, o.Status == ExtractStatus.Skipped ? "" : o.Text,
            o.Status == ExtractStatus.Skipped ? o.Reason ?? AttachmentReasons.Damaged : null);
    }

    private static Outcome RenderItem(AiAttachmentRead.Item item, TimeZoneInfo zone)
    {
        try
        {
            var sent = item.Sent is { } s ? TimeZoneInfo.ConvertTime(s, zone) : (DateTimeOffset?)null;
            return Outcome.From(AttachmentText.FromMailItem(item.Subject, item.From, sent, item.Body,
                item.AttachmentNames ?? []));
        }
        catch (Exception e)
        {
            return Outcome.Skip(AttachmentReasons.Damaged, e.GetType().Name);
        }
    }

    /// <summary>SPEC 26.3 cloud attachment checks, in order. Null when the item is dropped as a duplicate.</summary>
    private async Task<Final?> ReadCloudAsync(PlannedAttachment p, AiAttachmentRead.Cloud cloud,
        IReadOnlyList<SyncedRoot> roots, HashSet<string> seenUrls, HashSet<string> seenPaths, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(cloud.DisplayName) ? p.Name : cloud.DisplayName.Trim();
        Final Skip(string reason) => Final.Skipped(p, name, AttachmentKind.Cloud, reason);

        var url = cloud.Url?.Trim();
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Skip(AttachmentReasons.NotSynced);
        if (!OneDriveLinks.TryParse(url, out var link)) return Skip(AttachmentReasons.NotMicrosoftLink);
        if (!p.SharedAllowed) return Skip(AttachmentReasons.FromOutside);
        if (seenUrls.Contains(link.NormalizedUrl)) return null;

        if (OneDriveMap.Map(link.Url, roots) is not MappedLink.Local local) return Skip(AttachmentReasons.NotSynced);
        if (seenPaths.Contains(local.Path)) return null;
        seenUrls.Add(link.NormalizedUrl);
        seenPaths.Add(local.Path);

        return FromOutcome(p, name, AttachmentKind.Cloud, await ReadLinkAsync(local, ct));
    }

    private Task<Outcome> ReadLinkAsync(MappedLink.Local local, CancellationToken ct) =>
        RunAsync(token =>
        {
            switch (_readSynced(local, AttachmentText.MaxFileBytes, token))
            {
                case SyncedFileResult.Bytes b:
                    return Outcome.From(_extract(b.FileName, b.Data, token));
                case SyncedFileResult.TooLarge:
                    return Outcome.Skip(AttachmentReasons.TooLarge);
                case SyncedFileResult.Failed f:
                    return Outcome.Skip(AttachmentReasons.Damaged, f.ErrorType);
                default:
                    return Outcome.Skip(AttachmentReasons.NotSynced);
            }
        }, ct);

    /// <summary>SPEC 26.5 per-file limit: runs the work on the thread pool and gives up after PerFileTimeout.</summary>
    private async Task<Outcome> RunAsync(Func<CancellationToken, Outcome> work, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => work(cts.Token), CancellationToken.None);
        try
        {
            return await task.WaitAsync(PerFileTimeout, _time, ct);
        }
        catch (TimeoutException)
        {
            return Outcome.Skip(AttachmentReasons.TookTooLong);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Outcome.Skip(AttachmentReasons.TookTooLong);
        }
        catch (Exception e)
        {
            return Outcome.Skip(AttachmentReasons.Damaged, e.GetType().Name);
        }
        finally
        {
            if (task.IsCompleted) cts.Dispose();
            else
            {
                cts.Cancel();
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception;
                    cts.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
    }

    private AttachmentSet Finish(List<Final> finals, long start)
    {
        var budget = finals
            .Where(f => f.Status != ExtractStatus.Skipped)
            .Select(f => new BudgetItem(f.Plan.Tier, f.Text.Length))
            .ToList();
        var keeps = AttachmentBudget.Allocate(budget);

        var items = new List<PromptAttachment>(finals.Count);
        var bi = 0;
        foreach (var f in finals)
        {
            var status = f.Status;
            var text = f.Text;
            var reason = f.Reason;
            if (status != ExtractStatus.Skipped)
            {
                var keep = keeps[bi++];
                if (keep is null)
                {
                    status = ExtractStatus.Skipped;
                    text = "";
                    reason = AttachmentReasons.TooMuchText;
                }
                else if (keep.Value < text.Length)
                {
                    text = AttachmentBudget.Cut(text, keep.Value);
                    status = ExtractStatus.Truncated;
                }
            }
            items.Add(new PromptAttachment(f.Name, f.Plan.Origin, f.Kind, status, text, reason, f.Plan.Tier,
                f.Plan.SenderName, f.Plan.SenderEmail, f.Plan.MessageTime, f.Plan.EntryId, f.Plan.Attachment?.FileName));
        }

        var notes = items.Select(NoteFor).Where(n => n is not null).Select(n => n!).ToList();
        var counts = new AttachmentCounts(
            items.Count(i => i.Status == ExtractStatus.Read),
            items.Count(i => i.Status == ExtractStatus.Truncated),
            items.Count(i => i.Status == ExtractStatus.Skipped),
            items.Count(i => i.Kind is AttachmentKind.Link or AttachmentKind.Cloud),
            items.Sum(i => i.Text.Length),
            Elapsed(start));
        return new AttachmentSet(items, notes, counts);
    }

    /// <summary>The SPEC 26.7 note for one final item, or null (tier 3, Read, or a reason without a note).</summary>
    public static AttachmentNote? NoteFor(PromptAttachment item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Tier == 3 || item.Status == ExtractStatus.Read) return null;
        var shared = item.Kind is AttachmentKind.Link or AttachmentKind.Cloud;
        AttachmentNote Note(AttachmentNoteKind kind, string? ext = null) => new(kind, item.Name, shared, ext);

        if (item.Status == ExtractStatus.Truncated) return Note(AttachmentNoteKind.Truncated);

        switch (item.Reason)
        {
            case AttachmentReasons.NotSynced: return Note(AttachmentNoteKind.NotSynced);
            case AttachmentReasons.FromOutside: return Note(AttachmentNoteKind.FromOutside);
            case AttachmentReasons.EarlierEmailGone: return Note(AttachmentNoteKind.EarlierEmailGone);
            case AttachmentReasons.PasswordProtected: return Note(AttachmentNoteKind.PasswordProtected);
            case AttachmentReasons.TooLarge: return Note(AttachmentNoteKind.TooLarge);
            case AttachmentReasons.NoText: return Note(AttachmentNoteKind.NoText);
            case AttachmentReasons.Damaged: return Note(AttachmentNoteKind.Damaged);
            case AttachmentReasons.TookTooLong:
            case AttachmentReasons.OutOfTime: return Note(AttachmentNoteKind.TookTooLong);
            case AttachmentReasons.TooMuchText:
            case AttachmentReasons.TooManyFiles: return Note(AttachmentNoteKind.TooMuch);
        }

        // "Clippy can't read .<ext> files" gets a note only for the formats Clippy names (SPEC 26.4).
        var probe = AttachmentReasons.CantRead("x");            // "<prefix>x<suffix>"
        var at = probe.IndexOf('x');
        var prefix = probe[..at];
        var suffix = probe[(at + 1)..];
        if (item.Reason is { } reason && reason.Length > prefix.Length + suffix.Length
            && reason.StartsWith(prefix, StringComparison.Ordinal) && reason.EndsWith(suffix, StringComparison.Ordinal))
        {
            var ext = reason[prefix.Length..^suffix.Length];
            if (AttachmentText.FormatOf("file." + ext) == AttachmentFormat.UnsupportedNoted
                && reason == AttachmentReasons.CantRead(ext))
                return Note(AttachmentNoteKind.Format, ext);
        }
        return null;
    }
}

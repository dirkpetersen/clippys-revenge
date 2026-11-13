using System.Globalization;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>Path = full saved path, FileName = the attachment's original file name, Size = bytes on disk.</summary>
public sealed record SavedAttachment(string Path, string FileName, long Size);

/// <summary>Exactly one of Saved / Error is non-null.</summary>
public sealed record AttachmentSaveOutcome(AttachmentInfo Requested, SavedAttachment? Saved, string? Error);

public interface IAttachmentSaver
{
    /// <summary>Live: find the email, list its attachments, select one, save it into <paramref name="dir"/> (already PathPolicy-checked).</summary>
    Task<SavedAttachment> SaveOneAsync(string id, string? filename, int? position, string dir, CancellationToken ct);

    /// <summary>Live: find the email once, then save each wanted entry. One outcome per entry, same order.
    /// Per-attachment failures are outcomes; email-level failures throw.</summary>
    Task<IReadOnlyList<AttachmentSaveOutcome>> SaveManyAsync(
        string id, IReadOnlyList<AttachmentInfo> wanted, string dir, CancellationToken ct);
}

public sealed record GetAttachmentResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("size")] long Size);

public sealed record SavedAttachmentEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size);

public sealed record FailedAttachmentEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("error")] string Error);

public sealed record SaveAttachmentsResult(
    [property: JsonPropertyName("saved")] IReadOnlyList<SavedAttachmentEntry> Saved,
    [property: JsonPropertyName("failed")] IReadOnlyList<FailedAttachmentEntry> Failed,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("note")] string? Note);

public sealed record SaveAttachmentsArgs
{
    public string? SaveDir { get; init; }
    public SearchArgs Search { get; init; } = new();
    public bool IncludeInline { get; init; }
}

/// <summary>get_attachment / save_attachments logic (SPEC §11.3, §11.4). Never touches attachment content.</summary>
public sealed class AttachmentExport
{
    public const int MaxShownPathLength = 200;

    private readonly IAttachmentSaver _saver;
    private readonly MailboxIndexes _indexes;
    private readonly Func<Settings> _settings;
    private readonly ActivityHub _hub;
    private readonly TimeZoneInfo _tz;

    public AttachmentExport(IAttachmentSaver saver, MailboxIndexes indexes, Func<Settings> settings,
                            ActivityHub hub, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hub);
        _saver = saver;
        _indexes = indexes;
        _settings = settings;
        _hub = hub;
        _tz = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>PathPolicy Write check. Returns the full, existing directory. Throws McpException.</summary>
    public string ResolveSaveDir(string? saveDir, string toolName)
    {
        var s = _settings();
        var path = string.IsNullOrWhiteSpace(saveDir) ? s.AttachmentDir : saveDir.Trim();
        path ??= "";
        var shown = path.Length > MaxShownPathLength ? path[..MaxShownPathLength] : path;
        var relativeTo = !string.IsNullOrWhiteSpace(s.AttachmentDir) && Path.IsPathFullyQualified(s.AttachmentDir)
            ? s.AttachmentDir : null;
        try
        {
            return PathPolicy.Check(path, s.EffectiveAllowedFolders(), PathMode.Write, relativeTo);
        }
        catch (PathNotAllowedException ex)
        {
            _hub.Event(ActivityKind.Blocked,
                $"{toolName} refused save_dir \"{shown}\": {PathPolicy.Describe(ex.Reason)}");
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"Could not use save_dir \"{shown}\": {ex.Message}");
        }
    }

    public async Task<GetAttachmentResult> GetAsync(string? id, string? filename, int? position, string? saveDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpException("id is required. Use the id from search_emails.");
        var dir = ResolveSaveDir(saveDir, "get_attachment");
        var saved = await _saver.SaveOneAsync(id.Trim(), filename, position, dir, ct).ConfigureAwait(false);
        return new GetAttachmentResult(saved.Path, saved.FileName, saved.Size);
    }

    public async Task<SaveAttachmentsResult> SaveAsync(SaveAttachmentsArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var dir = ResolveSaveDir(args.SaveDir, "save_attachments");
        var search = args.Search ?? new SearchArgs();
        var filter = search.ToFilter(_tz);
        var hits = _indexes.Search(filter, search.Mailbox);

        var plan = new List<(MailboxHit Hit, IReadOnlyList<AttachmentInfo> Wanted)>();
        foreach (var hit in hits)
        {
            var wanted = AttachmentSelection.SelectForBulk(
                hit.Email.Attachments, search.AttachmentName, search.AttachmentExt, args.IncludeInline);
            if (wanted.Count > 0) plan.Add((hit, wanted));
        }

        var saved = new List<SavedAttachmentEntry>();
        var failed = new List<FailedAttachmentEntry>();

        void Fail(MailboxHit hit, AttachmentInfo a, string error) =>
            failed.Add(new FailedAttachmentEntry(hit.Email.Id, hit.Mailbox, a.Position, a.FileName, error));

        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (hit, wanted) = plan[i];
            try
            {
                var outcomes = await _saver.SaveManyAsync(hit.Email.Id, wanted, dir, ct).ConfigureAwait(false);
                for (var k = 0; k < wanted.Count; k++)
                {
                    var o = k < outcomes.Count ? outcomes[k] : null;
                    var req = o?.Requested ?? wanted[k];
                    if (o?.Saved is { } sv)
                        saved.Add(new SavedAttachmentEntry(hit.Email.Id, hit.Mailbox, req.Position, req.FileName, sv.Path, sv.Size));
                    else
                        Fail(hit, req, o?.Error ?? "The attachment was not saved.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OutlookUnavailableException ex)
            {
                for (var j = i; j < plan.Count; j++)
                    foreach (var a in plan[j].Wanted) Fail(plan[j].Hit, a, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                foreach (var a in wanted) Fail(hit, a, ex.Message);
            }
        }

        string? note = null;
        if (hits.Count == 0)
            note = "No emails matched the filters.";
        else if (plan.Count == 0)
            note = $"{hits.Count.ToString(CultureInfo.InvariantCulture)} email(s) matched, but none had attachments to save. Inline attachments are skipped unless include_inline is true.";
        return new SaveAttachmentsResult(saved, failed, saved.Count, note);
    }
}

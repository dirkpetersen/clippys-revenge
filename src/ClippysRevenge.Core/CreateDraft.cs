using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public sealed record CreateDraftArgs(
    string? To,
    string? Subject,
    string? Body,
    string? Cc = null,
    string? Bcc = null,
    bool Html = false,
    IReadOnlyList<string?>? Attachments = null,
    string? Mailbox = null);

/// <summary>Everything the COM side needs; produced only by <see cref="CreateDraftTool.Plan"/>.</summary>
public sealed record DraftPlan(
    string Mailbox,
    IReadOnlyList<Recipient> To,
    IReadOnlyList<Recipient> Cc,
    IReadOnlyList<Recipient> Bcc,
    string Subject,
    string HtmlFragment,
    bool SignatureAdded,
    IReadOnlyList<string> AttachmentPaths,
    string Marker = DraftMarker.Tool);

/// <summary>MarkerSet is false when the ClippyDraft property couldn't be set (the draft was still saved).</summary>
public sealed record CreatedDraft(string EntryId, string Subject, bool MarkerSet = true);

public interface IDraftWriter
{
    /// <summary>Creates and saves the draft. Throws McpException (incl. OutlookUnavailableException) on failure.</summary>
    Task<CreatedDraft> CreateAsync(DraftPlan plan, CancellationToken ct);
}

public sealed record CreateDraftResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("signature_added")] bool SignatureAdded);

public sealed class CreateDraftTool
{
    public const int MaxEventSubjectLength = 80;

    private readonly Settings _settings;
    private readonly Signatures _signatures;
    private readonly IDraftWriter _writer;
    private readonly ActivityHub _hub;

    public CreateDraftTool(Settings settings, Signatures signatures, IDraftWriter writer, ActivityHub hub)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(hub);
        _settings = settings;
        _signatures = signatures;
        _writer = writer;
        _hub = hub;
    }

    public DraftPlan Plan(CreateDraftArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // 1. Mailbox
        var key = MailboxIndexes.Normalize(args.Mailbox);
        if (key == MailboxIndexes.AllMailboxes)
            throw new McpException("Mailbox \"all\" only works with search tools. Use your own mailbox (leave mailbox empty) or one shared mailbox address.");
        if (key.Length != 0)
        {
            var found = _settings.FindSharedMailbox(key);
            if (found is null || !found.Enabled)
            {
                var keys = new List<string> { "" };
                if (_settings.SharedMailboxes is not null)
                    foreach (var sm in _settings.SharedMailboxes)
                    {
                        if (sm is null || !sm.Enabled) continue;
                        var k = MailboxIndexes.Normalize(sm.Smtp);
                        if (k.Length > 0 && !keys.Contains(k, StringComparer.Ordinal)) keys.Add(k);
                    }
                throw MailboxIndexes.Unknown(args.Mailbox, keys);
            }
        }
        var eventMailbox = key.Length == 0 ? null : key;

        // 2. Recipients
        var to = ParseList(args.To, "to");
        var cc = ParseList(args.Cc, "cc");
        var bcc = ParseList(args.Bcc, "bcc");
        if (to.Count == 0)
            throw new McpException("create_draft needs at least one recipient in \"to\", like \"Sample, Bob <bob.sample@example.org>; someone@example.org\".");

        // 3. Subject
        var subject = (args.Subject ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

        // 4. Attachments
        var roots = _settings.EffectiveAllowedFolders();
        var relativeTo = !string.IsNullOrWhiteSpace(_settings.AttachmentDir) && Path.IsPathFullyQualified(_settings.AttachmentDir)
            ? _settings.AttachmentDir
            : null;
        var paths = new List<string>();
        if (args.Attachments is not null)
        {
            for (var n = 0; n < args.Attachments.Count; n++)
            {
                var i = n + 1;
                var entry = args.Attachments[n];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    var folders = roots.Count == 0 ? "(none)" : string.Join("; ", roots);
                    throw new McpException($"Attachment {i} is empty. Pass the full path of an existing file in the allowed folders: {folders}.");
                }
                try
                {
                    paths.Add(PathPolicy.Check(entry, roots, PathMode.Read, relativeTo));
                }
                catch (PathNotAllowedException ex)
                {
                    _hub.Event(ActivityKind.Blocked,
                        $"create_draft: attachment {i} refused ({PathPolicy.Describe(ex.Reason)})", mailbox: eventMailbox);
                    throw new McpException(ex.Message + " Nothing was created.");
                }
                catch (FileNotFoundException ex)
                {
                    throw new McpException($"Attachment {i}: {ex.Message} Nothing was created.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new McpException($"Attachment {i} could not be checked: {ex.Message} Nothing was created.");
                }
            }
        }

        // 5. Body
        var sig = _signatures.Default(SignatureKind.New);
        var fragment = DraftBody.Fragment(args.Body ?? "", args.Html, sig);
        var signatureAdded = DraftBody.SignatureContent(sig) is not null;

        return new DraftPlan(key, to, cc, bcc, subject, fragment, signatureAdded, paths, DraftMarker.Tool);
    }

    public async Task<CreateDraftResult> RunAsync(CreateDraftArgs args, CancellationToken ct)
    {
        var plan = Plan(args);
        var created = await _writer.CreateAsync(plan, ct).ConfigureAwait(false);
        _hub.Event(ActivityKind.Draft, $"draft created: {Short(created.Subject)}",
            mailbox: plan.Mailbox.Length == 0 ? null : plan.Mailbox);
        return new CreateDraftResult(created.EntryId, created.Subject, plan.Mailbox, plan.SignatureAdded);
    }

    private static IReadOnlyList<Recipient> ParseList(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return Recipients.Parse(raw);
        }
        catch (FormatException ex)
        {
            throw new McpException($"\"{field}\": {ex.Message}");
        }
    }

    private static string Short(string subject)
    {
        if (string.IsNullOrEmpty(subject)) return "(no subject)";
        return subject.Length > MaxEventSubjectLength ? subject[..MaxEventSubjectLength] + "…" : subject;
    }
}

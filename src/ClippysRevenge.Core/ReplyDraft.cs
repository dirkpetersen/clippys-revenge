using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>Validated input for the COM side. <see cref="Mailbox"/> is null when the original's mailbox is to be used.</summary>
public sealed record ReplyPlan(
    string Id,
    string? Mailbox,
    bool ReplyAll,
    string HtmlFragment,
    bool SignatureAdded,
    string Marker = DraftMarker.Tool,                     // "tool" for reply_draft, "ai" for AI drafts (M11.7)
    WorkPriority Priority = WorkPriority.Interactive);    // Background for AI drafts (SPEC 19.9)

/// <summary>Mailbox is the normalized key the draft ended up in ("" = own).</summary>
public sealed record RepliedDraft(string EntryId, string Subject, string Mailbox);

public interface IReplyDraftWriter
{
    /// <summary>Creates and saves the reply draft. Throws McpException (incl. ItemNotFoundException) on failure.</summary>
    Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct);
}

public sealed class ReplyDraftTool
{
    private readonly Settings _settings;
    private readonly Signatures _signatures;
    private readonly IReplyDraftWriter _writer;
    private readonly ActivityHub _hub;

    public ReplyDraftTool(Settings settings, Signatures signatures, IReplyDraftWriter writer, ActivityHub hub)
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

    public ReplyPlan Plan(string? id, string? body, bool replyAll, bool html, string? mailbox)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpException("reply_draft needs the \"id\" of the email to reply to, as returned by search_emails.");

        string? target = null;
        var key = MailboxIndexes.Normalize(mailbox);
        if (key == MailboxIndexes.AllMailboxes)
            throw new McpException("Mailbox \"all\" only works with search tools. Leave mailbox empty to use the mailbox the email is in, or pass one shared mailbox address.");
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
                throw MailboxIndexes.Unknown(mailbox, keys);
            }
            target = key;
        }

        var sig = _signatures.Default(SignatureKind.Reply);
        var fragment = DraftBody.Fragment(body ?? "", html, sig);
        return new ReplyPlan(id.Trim(), target, replyAll, fragment, DraftBody.SignatureContent(sig) is not null,
            DraftMarker.Tool, WorkPriority.Interactive);
    }

    public async Task<CreateDraftResult> RunAsync(string? id, string? body, bool replyAll, bool html, string? mailbox, CancellationToken ct)
    {
        var plan = Plan(id, body, replyAll, html, mailbox);
        var made = await _writer.ReplyAsync(plan, ct).ConfigureAwait(false);
        _hub.Event(ActivityKind.Draft, $"reply draft created: {Short(made.Subject)}",
            mailbox: made.Mailbox.Length == 0 ? null : made.Mailbox);
        return new CreateDraftResult(made.EntryId, made.Subject, made.Mailbox, plan.SignatureAdded);
    }

    private static string Short(string subject)
    {
        if (string.IsNullOrEmpty(subject)) return "(no subject)";
        return subject.Length > CreateDraftTool.MaxEventSubjectLength
            ? subject[..CreateDraftTool.MaxEventSubjectLength] + "…" : subject;
    }
}

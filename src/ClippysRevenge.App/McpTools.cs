using System.ComponentModel;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

[McpServerToolType]
internal sealed partial class McpTools(ActivityHub hub, MailboxIndexes indexes, OutlookMail mail, OutlookWorker worker)
{
    [McpServerTool(Name = "search_emails", ReadOnly = true, Idempotent = true)]
    [Description("Searches the local email index (fast, works while Outlook is busy). All filters are optional and combine with AND; results are newest first. Each result has id, mailbox, folder, date (local time), from, sender_email, to, subject, has_attachments, attachments[] (name, size, inline flag) and snippet. Pass a result's id to get_message to read the full body. Only synced mail is indexed; use index_status to check coverage if something seems missing.")]
    public IReadOnlyList<SearchResultDto> SearchEmails(
        [Description("Free text over subject, body, sender, recipients and attachment file names. Words are ANDed; use OR between words for alternatives, \"quotes\" for a phrase, and a trailing * for a prefix.")] string? query = null,
        [Description("Substring of the sender's name or email address.")] string? sender = null,
        [Description("Substring of To or CC. Also accepts a list like 'Ann <ann@x.edu>; bob@x.edu', which matches mail involving any of them.")] string? recipient = null,
        [Description("Substring of the subject.")] string? subject = null,
        [Description("Earliest date, inclusive: ISO date (2026-03-01) or date-time (2026-03-01T14:30), local time.")] string? date_from = null,
        [Description("Latest date, inclusive. A date-only value includes the whole day.")] string? date_to = null,
        [Description("'inbox', 'sent', or another indexed folder path. Subfolders are not included. Empty = all folders.")] string? folder = null,
        [Description("true = only mail with real (non-inline) attachments, false = only mail without.")] bool? has_attachment = null,
        [Description("Substring of any attachment file name.")] string? attachment_name = null,
        [Description("Exact attachment extension, with or without the dot (pdf or .pdf).")] string? attachment_ext = null,
        [Description("true = only unread, false = only read.")] bool? unread = null,
        [Description("Empty = the user's own mailbox, an SMTP address = that shared mailbox, 'all' = every enabled mailbox.")] string? mailbox = null,
        [Description("Maximum results, 1 to 200.")] int limit = 50,
        [Description("Number of results to skip, for paging.")] int offset = 0) =>
        MailQueries.Search(indexes, TimeZoneInfo.Local, query, sender, recipient, subject, date_from, date_to,
            folder, has_attachment, attachment_name, attachment_ext, unread, mailbox, limit, offset);

    [McpServerTool(Name = "get_message", ReadOnly = true, Idempotent = true)]
    [Description("Returns one email in full (headers, complete body, attachment list) by the id from search_emails. Reads the index first; if the email is not indexed yet, reads it live from Outlook, which needs Outlook to be running. The mailbox is found from the id.")]
    public async Task<MessageDto> GetMessage(
        [Description("The id of an email, as returned by search_emails.")] string id,
        CancellationToken cancellationToken = default)
    {
        var tz = TimeZoneInfo.Local;
        if (MailQueries.GetIndexed(indexes, tz, id) is { } hit)
        {
            return hit;
        }
        var live = await worker.RunAsync(app => mail.ReadById(app, id), WorkPriority.Interactive, cancellationToken)
            .ConfigureAwait(false);
        return MailQueries.FromLive(live, tz);
    }

    [McpServerTool(Name = "index_status", ReadOnly = true, Idempotent = true)]
    [Description("Shows the state of the local email index: per-mailbox counts, oldest and newest indexed mail, last sync and whether the initial backfill is complete, plus whether Outlook is running and what Clippy is doing right now. Use it to explain why a search found nothing or to check indexing progress.")]
    public IndexStatusDto IndexStatus(
        [Description("Optional SMTP address of a shared mailbox. Empty = all configured mailboxes.")] string? mailbox = null)
    {
        bool? running = worker.State switch
        {
            OutlookConnectionState.Available => true,
            OutlookConnectionState.Unavailable => false,
            _ => null,
        };
        return MailQueries.Status(indexes, hub, TimeZoneInfo.Local, running, mailbox);
    }
}

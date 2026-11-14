using System.ComponentModel;
using System.Diagnostics;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

internal sealed partial class McpTools
{
    [McpServerTool(Name = "email_stats", ReadOnly = true, Idempotent = true)]
    [Description(EmailStats.ToolDescription)]
    public EmailStatsDto GetEmailStats(
        Optional<MailboxIndexes> index,
        [Description(EmailStats.GroupByParameterDescription)] string group_by,
        [Description(EmailStats.TopParameterDescription)] int top = EmailStats.DefaultTop,
        [Description(EmailStats.OrderParameterDescription)] string? order = "count",
        [Description(EmailStats.IncludeCcParameterDescription)] bool include_cc = true,
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
        [Description(EmailStats.MailboxParameterDescription)] string? mailbox = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var dto = EmailStats.Run(index.Required(), TimeZoneInfo.Local,
            new SearchArgs
            {
                Query = query, Sender = sender, Recipient = recipient, Subject = subject,
                DateFrom = date_from, DateTo = date_to, Folder = folder, HasAttachment = has_attachment,
                AttachmentName = attachment_name, AttachmentExt = attachment_ext, Unread = unread, Mailbox = mailbox,
            },
            group_by, top, order, include_cc, cancellationToken);
        log.LogDebug("email_stats: group_by {GroupBy}, mailbox {Mailbox}, {Total} emails, {Groups} groups, {Ms} ms",
            dto.GroupBy, MailboxLabel(dto.Mailbox), dto.Total, dto.Distinct, sw.ElapsedMilliseconds);
        return dto;
    }

    [McpServerTool(Name = "query_index", ReadOnly = true, Idempotent = true)]
    [Description(IndexQueryTexts.ToolDescription)]
    public IndexQueryResult QueryIndex(
        Optional<MailboxIndexes> index,
        [Description(IndexQueryTexts.SqlParameterDescription)] string sql,
        [Description(IndexQueryTexts.MailboxParameterDescription)] string? mailbox = null,
        [Description(IndexQueryTexts.MaxRowsParameterDescription)] int max_rows = IndexQuery.DefaultMaxRows,
        CancellationToken cancellationToken = default)
    {
        var result = IndexQuery.Run(index.Required(), sql, mailbox, max_rows, cancellationToken);
        var key = MailboxIndexes.Normalize(mailbox);
        log.LogDebug("query_index: {Rows} rows, {Columns} columns, mailbox {Mailbox}, {Ms} ms, truncated {Reason}, {Cells} cells cut",
            result.RowCount, result.Columns.Count, MailboxLabel(key), result.ElapsedMs, result.TruncatedReason ?? "no", result.TruncatedCells);
        return result;
    }
}
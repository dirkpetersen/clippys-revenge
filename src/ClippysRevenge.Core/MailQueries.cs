using System.Globalization;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public sealed record AttachmentDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("inline")] bool Inline);

public sealed record SearchResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("sender_email")] string SenderEmail,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("has_attachments")] bool HasAttachments,
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentDto> Attachments,
    [property: JsonPropertyName("snippet")] string Snippet);

public sealed record MessageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("sender_email")] string SenderEmail,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("cc")] string Cc,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("unread")] bool Unread,
    [property: JsonPropertyName("has_attachments")] bool HasAttachments,
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentDto> Attachments,
    [property: JsonPropertyName("internet_message_id")] string? InternetMessageId,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] string Source);

public sealed record MailboxStatusDto(
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("total_emails")] long TotalEmails,
    [property: JsonPropertyName("folders")] IReadOnlyDictionary<string, long> Folders,
    [property: JsonPropertyName("oldest")] string? Oldest,
    [property: JsonPropertyName("newest")] string? Newest,
    [property: JsonPropertyName("db_size_bytes")] long DbSizeBytes,
    [property: JsonPropertyName("last_sync")] string? LastSync,
    [property: JsonPropertyName("backfill_complete")] bool BackfillComplete);

public sealed record IndexStatusDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("progress")] string? Progress,
    [property: JsonPropertyName("outlook_running")] bool? OutlookRunning,
    [property: JsonPropertyName("mailboxes")] IReadOnlyList<MailboxStatusDto> Mailboxes);

/// <summary>Pure logic behind search_emails, get_message and index_status (SPEC §11.1, §11.2, §11.10).</summary>
public static class MailQueries
{
    private static string Iso(DateTimeOffset t, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(t, tz).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static AttachmentDto[] Map(IReadOnlyList<AttachmentInfo> a) =>
        a.Select(x => new AttachmentDto(x.Position, x.FileName, x.Size, x.IsInline)).ToArray();

    public static IReadOnlyList<SearchResultDto> Search(
        MailboxIndexes indexes, TimeZoneInfo tz,
        string? query, string? sender, string? recipient, string? subject, string? dateFrom, string? dateTo,
        string? folder, bool? hasAttachment, string? attachmentName, string? attachmentExt, bool? unread,
        string? mailbox, int limit, int offset)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        SearchFilter filter;
        try
        {
            filter = new SearchFilter
            {
                Query = query,
                Sender = sender,
                Recipient = recipient,
                Subject = subject,
                DateFrom = SearchDates.ParseFrom(dateFrom, tz),
                DateTo = SearchDates.ParseTo(dateTo, tz),
                Folder = folder,
                HasAttachment = hasAttachment,
                AttachmentName = attachmentName,
                AttachmentExt = attachmentExt,
                Unread = unread,
                Limit = Math.Clamp(limit, 1, SearchFilter.MaxLimit),
                Offset = Math.Max(offset, 0),
            };
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
        return indexes.Search(filter, mailbox).Select(h => new SearchResultDto(
            h.Email.Id, h.Mailbox, h.Email.Folder, Iso(h.Email.Date, tz), h.Email.From, h.Email.SenderEmail,
            h.Email.To, h.Email.Subject, h.Email.HasAttachments, Map(h.Email.Attachments), h.Email.Snippet)).ToList();
    }

    /// <summary>The indexed email for an id, or null when no enabled index has it (the caller then reads via COM).</summary>
    public static MessageDto? GetIndexed(MailboxIndexes indexes, TimeZoneInfo tz, string? id)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        if (indexes.Locate(id) is not { } l) return null;
        var e = l.Email;
        return new MessageDto(e.EntryId, l.Mailbox, e.Folder, Iso(e.EventTime, tz), e.SenderName ?? "",
            e.SenderEmail ?? "", e.To ?? "", e.Cc ?? "", e.Subject ?? "", e.Unread, e.HasAttachments,
            Map(e.Attachments), e.InternetMessageId, e.Body ?? "", "index");
    }

    public static MessageDto FromLive(LiveEmail live, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(live);
        var e = live.Email;
        return new MessageDto(e.EntryId, live.Mailbox, e.Folder, Iso(e.EventTime, tz), e.SenderName ?? "",
            e.SenderEmail ?? "", e.To ?? "", e.Cc ?? "", e.Subject ?? "", e.Unread,
            e.Attachments.Any(a => !a.IsInline), Map(e.Attachments), e.InternetMessageId, e.Body ?? "", "outlook");
    }

    public static IndexStatusDto Status(
        MailboxIndexes? indexes, ActivityHub hub, TimeZoneInfo tz, bool? outlookRunning, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(hub);
        // indexes == null: the index could not be opened; still report state and Outlook status (SPEC 11.10).
        IReadOnlyList<string> keys = indexes is null ? []
            : string.IsNullOrWhiteSpace(mailbox)
            ? indexes.Mailboxes
            : [MailboxIndexes.Normalize(mailbox)];
        var list = new List<MailboxStatusDto>();
        foreach (var k in keys)
        {
            var s = indexes!.For(k).Status();
            list.Add(new MailboxStatusDto(k, s.TotalEmails, s.FolderCounts,
                s.Oldest is { } o ? Iso(o, tz) : null, s.Newest is { } n ? Iso(n, tz) : null,
                s.DbSizeBytes, s.LastSync is { } ls ? Iso(ls, tz) : null, s.BackfillComplete));
        }
        var p = hub.Progress;
        var progress = p.Total > 0 ? $"{p.Label} {p.Done}/{p.Total}" : p.Label.Length > 0 ? p.Label : null;
        return new IndexStatusDto(hub.State.ToString(), progress, outlookRunning, list);
    }
}

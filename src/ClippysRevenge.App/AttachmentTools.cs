using System.ComponentModel;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

[McpServerToolType]
internal sealed class AttachmentTools(Optional<AttachmentExport> exports)
{
    [McpServerTool(Name = "get_attachment")]
    [Description("Saves one attachment of an email to disk and returns its path. Use the id from search_emails. Pass filename or position when the email has more than one attachment. save_dir defaults to the user's attachment folder; relative paths go under it; it must be inside an allowed folder.")]
    public Task<GetAttachmentResult> GetAttachment(
        [Description("The id of an email, as returned by search_emails.")] string id,
        [Description("File name of the attachment (case-insensitive, whole name).")] string? filename = null,
        [Description("1-based position of the attachment in the email's attachments list.")] int? position = null,
        [Description("Folder to save into. Empty = the user's attachment folder. Relative paths go under it.")] string? save_dir = null,
        CancellationToken cancellationToken = default) =>
        exports.Required().GetAsync(id, filename, position, save_dir, cancellationToken);

    [McpServerTool(Name = "save_attachments")]
    [Description("Finds emails with the same filters as search_emails and saves their attachments into one folder (duplicates become 'name (2).ext'). attachment_name and attachment_ext also choose which attachments are saved. Inline images are skipped unless include_inline is true. limit is the number of emails (max 200).")]
    public Task<SaveAttachmentsResult> SaveAttachments(
        [Description("Folder to save into. Empty = the user's attachment folder. Relative paths go under it.")] string? save_dir = null,
        [Description("Free text over subject, body, sender, recipients and attachment file names.")] string? query = null,
        [Description("Substring of the sender's name or email address.")] string? sender = null,
        [Description("Substring of To or CC.")] string? recipient = null,
        [Description("Substring of the subject.")] string? subject = null,
        [Description("Earliest date, inclusive: ISO date or date-time, local time.")] string? date_from = null,
        [Description("Latest date, inclusive. A date-only value includes the whole day.")] string? date_to = null,
        [Description("'inbox', 'sent', or another indexed folder path. Empty = all folders.")] string? folder = null,
        [Description("true = only mail with real attachments, false = only mail without.")] bool? has_attachment = null,
        [Description("Substring of the attachment file name; only matching attachments are saved.")] string? attachment_name = null,
        [Description("Attachment extension, with or without the dot; only matching attachments are saved.")] string? attachment_ext = null,
        [Description("true = only unread, false = only read.")] bool? unread = null,
        [Description("Empty = the user's own mailbox, an SMTP address = that shared mailbox, 'all' = every enabled mailbox.")] string? mailbox = null,
        [Description("true = also save inline attachments such as embedded images.")] bool include_inline = false,
        [Description("Maximum number of emails, 1 to 200.")] int limit = 50,
        [Description("Number of emails to skip, for paging.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        exports.Required().SaveAsync(new SaveAttachmentsArgs
        {
            SaveDir = save_dir,
            IncludeInline = include_inline,
            Search = new SearchArgs
            {
                Query = query,
                Sender = sender,
                Recipient = recipient,
                Subject = subject,
                DateFrom = date_from,
                DateTo = date_to,
                Folder = folder,
                HasAttachment = has_attachment,
                AttachmentName = attachment_name,
                AttachmentExt = attachment_ext,
                Unread = unread,
                Mailbox = mailbox,
                Limit = limit,
                Offset = offset,
            },
        }, cancellationToken);
}

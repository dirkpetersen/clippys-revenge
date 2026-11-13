using System.ComponentModel;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

internal sealed partial class McpTools
{
    [McpServerTool(Name = "create_draft", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [Description("Creates an email draft in Outlook and saves it in the Drafts folder. It never sends: the user reviews and sends it from Outlook. to/cc/bcc take \"Name <email>; other@example.org\" lists separated by \";\". body is plain text unless html=true (then an HTML fragment). The user's default signature is added below the text when one is set. attachments are full paths to existing files in the allowed folders (relative paths resolve under the attachment folder); every path is checked before anything is created. mailbox: empty = the user's own mailbox, or the SMTP address of an enabled shared mailbox (the draft goes to its Drafts folder and is sent on its behalf).")]
    public Task<CreateDraftResult> CreateDraft(
        CreateDraftTool drafts,
        [Description("Recipients, e.g. \"Petersen, Dirk <dirk@example.org>; bob@example.org\". Separate with ;.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Plain text body (or an HTML fragment when html=true).")] string body,
        [Description("Optional, same format as to.")] string? cc = null,
        [Description("Optional, same format as to.")] string? bcc = null,
        [Description("true when body is HTML.")] bool html = false,
        [Description("Optional full file paths to attach.")] string[]? attachments = null,
        [Description("Optional SMTP address of an enabled shared mailbox. Empty = own mailbox.")] string? mailbox = null,
        CancellationToken cancellationToken = default) =>
        drafts.RunAsync(new CreateDraftArgs(to, subject, body, cc, bcc, html, attachments, mailbox), cancellationToken);
}

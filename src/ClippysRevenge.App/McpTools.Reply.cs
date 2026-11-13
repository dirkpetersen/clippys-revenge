using System.ComponentModel;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

internal sealed partial class McpTools
{
    [McpServerTool(Name = "reply_draft", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [Description("Creates a reply draft to an email and saves it in the Drafts folder. It never sends: the user reviews and sends it from Outlook. The subject starts with \"Re:: \". body is plain text unless html=true (then an HTML fragment); it is placed above the quoted original, followed by the user's reply signature when one is set. mailbox: empty = the mailbox the original email is in, or the SMTP address of an enabled shared mailbox (the draft goes to its Drafts folder and is sent on its behalf).")]
    public Task<CreateDraftResult> ReplyDraft(
        ReplyDraftTool replies,
        [Description("Id of the email to reply to, from search_emails.")] string id,
        [Description("Plain text reply (or an HTML fragment when html=true).")] string body,
        [Description("true to reply to all recipients.")] bool reply_all = false,
        [Description("true when body is HTML.")] bool html = false,
        [Description("Optional SMTP address of an enabled shared mailbox. Empty = the original's mailbox.")] string? mailbox = null,
        CancellationToken cancellationToken = default) =>
        replies.RunAsync(id, body, reply_all, html, mailbox, cancellationToken);
}

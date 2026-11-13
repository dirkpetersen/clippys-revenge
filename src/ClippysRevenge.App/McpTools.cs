using System.ComponentModel;
using System.Text.Json.Serialization;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

[McpServerToolType]
public sealed class McpTools(ActivityHub hub)
{
    [McpServerTool(Name = "index_status", ReadOnly = true, Idempotent = true)]
    [Description("Shows the state of the local email index: per-mailbox counts and sync progress, whether Outlook is running, and what Clippy is doing right now. Use it to explain why a search found nothing or to check indexing progress.")]
    public IndexStatusResult IndexStatus(
        [Description("Optional SMTP address of a shared mailbox. Empty = all configured mailboxes.")] string? mailbox = null) =>
        new(hub.State.ToString(), null, [], "The email index is not available yet.");
}

public sealed record IndexStatusResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("outlook_running")] bool? OutlookRunning,
    [property: JsonPropertyName("mailboxes")] IReadOnlyList<object> Mailboxes,
    [property: JsonPropertyName("note")] string? Note);

using System.Text.Json;

namespace ClippysRevenge.Core;

/// <summary>The model abstraction every AI feature calls (SPEC 19.3).</summary>
public interface IDraftModel
{
    /// <summary>Never throws except OperationCanceledException when ct is cancelled. Makes no model call.</summary>
    Task<ModelStatus> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Never throws (ArgumentNullException for a null request aside). Cancellation gives Failed(Cancelled).
    /// </summary>
    Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct);
}

public sealed record ModelRequest(string SystemPrompt, string UserPrompt, string JsonSchema,
    string Model, string Effort, TimeSpan Timeout, int MaxOutputTokens);

public enum ModelFailure { NotInstalled, Timeout, Cancelled, BadInput, BadOutput, NoStructuredOutput, ProcessExit, Http }

public abstract record ModelResult
{
    private ModelResult() { }

    /// <summary>Output is a JSON object, already Clone()d (it outlives its JsonDocument).</summary>
    public sealed record Ok(JsonElement Output, TimeSpan Duration, int? InputTokens, int? OutputTokens) : ModelResult;

    /// <summary>RetryAt null = unknown (the caller waits 60 min, SPEC 19.7).</summary>
    public sealed record RateLimited(DateTimeOffset? RetryAt) : ModelResult;

    public sealed record NotSignedIn : ModelResult;

    /// <summary>Code: the process exit code (ProcessExit) or the HTTP status (Http); else null.</summary>
    public sealed record Failed(ModelFailure Kind, int? Code) : ModelResult;
}

public sealed record ModelStatus(bool Ready, string Text);

public static class DraftModel
{
    /// <summary>SPEC 19.7 reply schema, compact, property order as in SPEC.</summary>
    public const string ReplySchema =
        """{"type":"object","additionalProperties":false,"required":["needs_reply","confidence","reason"],"properties":{"needs_reply":{"type":"boolean"},"confidence":{"type":"number","minimum":0,"maximum":1},"reply_all":{"type":"boolean"},"body_html":{"type":"string"},"reason":{"type":"string","maxLength":200}}}""";

    /// <summary>SPEC 19.6 style schema (used by M11.6).</summary>
    public const string StyleSchema =
        """{"type":"object","additionalProperties":false,"required":["body_markdown"],"properties":{"body_markdown":{"type":"string"}}}""";

    public const int DraftMaxOutputTokens = 8_000;
    public const int StyleMaxOutputTokens = 16_000;
    public static readonly TimeSpan StyleTimeout = TimeSpan.FromSeconds(600);
    public const int MaxBodyHtmlChars = 50_000;
}

/// <summary>The validated, used part of a reply. `reason` is validated for type and then dropped (SPEC 19.7).</summary>
public sealed record DraftReply(bool NeedsReply, double Confidence, bool? ReplyAll, string? BodyHtml)
{
    /// <summary>Null when invalid (the caller treats it as BadOutput). Never throws.</summary>
    public static DraftReply? Parse(JsonElement output)
    {
        try
        {
            if (output.ValueKind != JsonValueKind.Object) return null;

            if (!output.TryGetProperty("needs_reply", out var nr)
                || (nr.ValueKind != JsonValueKind.True && nr.ValueKind != JsonValueKind.False)) return null;
            var needsReply = nr.ValueKind == JsonValueKind.True;

            if (!output.TryGetProperty("confidence", out var cf) || cf.ValueKind != JsonValueKind.Number) return null;
            if (!cf.TryGetDouble(out var confidence) || !double.IsFinite(confidence)
                || confidence < 0.0 || confidence > 1.0) return null;

            bool? replyAll = null;
            if (output.TryGetProperty("reply_all", out var ra) && ra.ValueKind != JsonValueKind.Null)
            {
                if (ra.ValueKind != JsonValueKind.True && ra.ValueKind != JsonValueKind.False) return null;
                replyAll = ra.ValueKind == JsonValueKind.True;
            }

            string? body = null;
            if (output.TryGetProperty("body_html", out var bh) && bh.ValueKind != JsonValueKind.Null)
            {
                if (bh.ValueKind != JsonValueKind.String) return null;
                body = bh.GetString();
                if (body is not null && body.Length > DraftModel.MaxBodyHtmlChars) return null;
            }

            if (output.TryGetProperty("reason", out var rs) && rs.ValueKind != JsonValueKind.Null
                && rs.ValueKind != JsonValueKind.String) return null;

            if (needsReply && string.IsNullOrWhiteSpace(body)) return null;

            return new DraftReply(needsReply, confidence, replyAll, body);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

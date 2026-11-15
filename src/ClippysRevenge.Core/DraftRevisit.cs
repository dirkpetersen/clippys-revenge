using System.Text.Json;

namespace ClippysRevenge.Core;

public enum RevisitAction { Keep, Replace, Remove }

/// <summary>The validated, used part of a revisit answer (SPEC 23.7). For Keep and Remove, ReplyAll, BodyHtml and
/// Meeting are always null (validated, then dropped). For Replace, BodyHtml is non-blank.</summary>
public sealed record RevisitReply(RevisitAction Action, double Confidence, bool? ReplyAll, string? BodyHtml,
    MeetingAsk? Meeting);

/// <summary>SPEC 23.2, 23.6, 23.7 constants and the parser.</summary>
public static class DraftRevisit
{
    public const int MaxDraftAgeDays = 14;          // SPEC 23.2
    public const int MaxRevisitsPerMailbox = 10;    // SPEC 23.2
    public const int MaxAttempts = 3;               // SPEC 23.6 step 5 give-up

    /// <summary>Null when invalid (the caller treats it as BadOutput). Never throws.</summary>
    public static RevisitReply? Parse(JsonElement output)
    {
        try
        {
            if (output.ValueKind != JsonValueKind.Object) return null;

            if (!output.TryGetProperty("action", out var ac) || ac.ValueKind != JsonValueKind.String) return null;
            var action = ac.GetString() switch
            {
                "keep" => RevisitAction.Keep,
                "replace" => RevisitAction.Replace,
                "remove" => RevisitAction.Remove,
                _ => (RevisitAction?)null,
            };
            if (action is null) return null;

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

            if (action == RevisitAction.Replace && string.IsNullOrWhiteSpace(body)) return null;
            if (action != RevisitAction.Replace) return new RevisitReply(action.Value, confidence, null, null, null);

            MeetingAsk? meetingAsk = null;
            if (output.TryGetProperty("meeting", out var mt)) meetingAsk = MeetingAsk.Parse(mt);

            return new RevisitReply(RevisitAction.Replace, confidence, replyAll, body, meetingAsk);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

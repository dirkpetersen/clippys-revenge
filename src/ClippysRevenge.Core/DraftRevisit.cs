using System.Text.Json;

namespace ClippysRevenge.Core;

public enum RevisitAction { Keep, Replace, Remove }

/// <summary>The validated, used part of a revisit answer (SPEC 23.7). For Keep and Remove, ReplyAll, BodyHtml, NoteForUser and
/// Meeting are always null (validated, then dropped). For Replace, BodyHtml is non-blank.</summary>
public sealed record RevisitReply(RevisitAction Action, double Confidence, bool? ReplyAll, string? BodyHtml,
    MeetingAsk? Meeting, string? NoteForUser = null);

/// <summary>SPEC 23.2, 23.6, 23.7 constants and the parser.</summary>
public static class DraftRevisit
{
    public const int MaxDraftAgeDays = 14;          // SPEC 23.2
    public const int MaxRevisitsPerMailbox = 10;    // SPEC 23.2
    public const int MaxAttempts = 3;               // SPEC 23.6 step 5 give-up

    /// <summary>Rows fetched with EmailIndex.Thread for a revisit prompt before PromptThread filters them.</summary>
    public const int ThreadFetch = 50;

    /// <summary>True when the trimmed address equals userSmtp or mailboxSmtp (OrdinalIgnoreCase). Null/blank gives false.</summary>
    public static bool IsOwnAddress(string? address, string? userSmtp, string? mailboxSmtp)
    {
        var a = address?.Trim();
        if (string.IsNullOrEmpty(a)) return false;
        var u = userSmtp?.Trim();
        var m = mailboxSmtp?.Trim();
        return (!string.IsNullOrEmpty(u) && string.Equals(a, u, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(m) && string.Equals(a, m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>SPEC 23.6 step 3: the first row (newest first) whose sender isn't own and that the index rules don't
    /// skip (EvaluateIndex gives null or not_direct). Null = none.</summary>
    /// <exception cref="ArgumentNullException">rows or settings is null.</exception>
    /// <exception cref="ArgumentException">mailboxSmtp is null or blank.</exception>
    public static DraftCandidate? PickReplyTarget(IReadOnlyList<DraftCandidate> newestFirst, string mailboxSmtp,
        string? userSmtp, AiDraftingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxSmtp);
        foreach (var row in newestFirst)
        {
            if (IsOwnAddress(row.SenderEmail, userSmtp, mailboxSmtp)) continue;
            var rule = DraftRules.EvaluateIndex(row, mailboxSmtp, settings, () => false);
            if (rule is null || rule == DraftRuleIds.NotDirect) return row;
        }
        return null;
    }

    /// <summary>The revisit &lt;thread&gt; rows: drops the old draft's own row and rows in Drafts or Deleted Items
    /// (first folder segment, OrdinalIgnoreCase). Order kept.</summary>
    /// <exception cref="ArgumentNullException">rows is null.</exception>
    public static IReadOnlyList<ThreadMessage> PromptThread(IReadOnlyList<ThreadMessage> rows, string? draftEntryId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<ThreadMessage>();
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(draftEntryId)
                && string.Equals(r.EntryId, draftEntryId, StringComparison.OrdinalIgnoreCase)) continue;
            var first = (r.Folder ?? "").Split('\\')[0].Trim();
            if (first.Equals("Drafts", StringComparison.OrdinalIgnoreCase)
                || first.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(r);
        }
        return result;
    }

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

            return new RevisitReply(RevisitAction.Replace, confidence, replyAll, body, meetingAsk, DraftModel.ParseNote(output));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

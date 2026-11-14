using System.Globalization;
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
        """{"type":"object","additionalProperties":false,"required":["needs_reply","confidence","reason"],"properties":{"needs_reply":{"type":"boolean"},"confidence":{"type":"number","minimum":0,"maximum":1},"reply_all":{"type":"boolean"},"body_html":{"type":"string"},"reason":{"type":"string","maxLength":200},"meeting":{"type":"object","additionalProperties":false,"required":["requested"],"properties":{"requested":{"type":"boolean"},"duration_minutes":{"type":"integer","minimum":15,"maximum":480},"earliest_date":{"type":"string","maxLength":10},"latest_date":{"type":"string","maxLength":10},"outside_hours_heading":{"type":"string","maxLength":80}}}}}""";

    /// <summary>SPEC 19.6 style schema (used by M11.6).</summary>
    public const string StyleSchema =
        """{"type":"object","additionalProperties":false,"required":["body_markdown"],"properties":{"body_markdown":{"type":"string"}}}""";

    public const int DraftMaxOutputTokens = 8_000;
    public const int StyleMaxOutputTokens = 16_000;
    public static readonly TimeSpan StyleTimeout = TimeSpan.FromSeconds(600);
    public const int MaxBodyHtmlChars = 50_000;
}

/// <summary>The validated, used part of a reply. `reason` is validated for type and then dropped (SPEC 19.7).
/// Meeting: null when no meeting was asked (SPEC 19.7, 21.6).</summary>
public sealed record DraftReply(bool NeedsReply, double Confidence, bool? ReplyAll, string? BodyHtml,
    MeetingAsk? Meeting = null)
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

            MeetingAsk? meetingAsk = null;
            if (output.TryGetProperty("meeting", out var mt)) meetingAsk = MeetingAsk.Parse(mt);

            return new DraftReply(needsReply, confidence, replyAll, body, meetingAsk);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>What the model said about a meeting (SPEC 19.7, 21.6). Only built when meeting.requested is true.</summary>
/// <param name="DurationMinutes">An integer in 15-480, else null (the caller uses DefaultDurationMinutes).</param>
/// <param name="EarliestDate">Exactly yyyy-MM-dd and a real date, else null.</param>
/// <param name="LatestDate">Same rule as EarliestDate. Not compared with EarliestDate here (M13.4 does the range).</param>
/// <param name="OutsideHoursHeading">The model's string as sent (not cleaned), or null when absent / not a string.
/// Cleaning and the fallback to the default heading are SPEC 21.4's rule, applied where the heading is used (M13.1/M13.4).</param>
public sealed record MeetingAsk(int? DurationMinutes, DateOnly? EarliestDate, DateOnly? LatestDate, string? OutsideHoursHeading)
{
    public const int DefaultDurationMinutes = 60;
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 480;

    /// <summary>The "meeting" property value. Null when it isn't an object or requested isn't JSON true.
    /// Never throws (any exception gives null).</summary>
    public static MeetingAsk? Parse(JsonElement meeting)
    {
        try
        {
            if (meeting.ValueKind != JsonValueKind.Object) return null;
            if (!meeting.TryGetProperty("requested", out var rq) || rq.ValueKind != JsonValueKind.True) return null;

            int? duration = null;
            if (meeting.TryGetProperty("duration_minutes", out var dm) && dm.ValueKind == JsonValueKind.Number
                && dm.TryGetInt32(out var mins) && mins >= MinDurationMinutes && mins <= MaxDurationMinutes)
                duration = mins;

            string? heading = null;
            if (meeting.TryGetProperty("outside_hours_heading", out var oh) && oh.ValueKind == JsonValueKind.String)
                heading = oh.GetString();

            return new MeetingAsk(duration, Date(meeting, "earliest_date"), Date(meeting, "latest_date"), heading);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateOnly? Date(JsonElement meeting, string name)
    {
        if (!meeting.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (s is null || s.Length != 10) return null;
        for (var i = 0; i < 10; i++)
        {
            var c = s[i];
            if (i is 4 or 7 ? c != '-' : c is < '0' or > '9') return null;
        }
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}

/// <summary>Picks the backend from AiDraftingSettings.Backend on every call (SPEC 19.2, 19.3).</summary>
public sealed class DraftModelSwitch : IDraftModel
{
    public const string ApiKeyUnavailableText = "✗ The API key backend isn't available in this version.";

    private readonly Func<AiDraftingSettings> _settings;
    private readonly IDraftModel _claudeCode;
    private readonly IDraftModel? _apiKey;

    /// <exception cref="ArgumentNullException">settings or claudeCode is null.</exception>
    public DraftModelSwitch(Func<AiDraftingSettings> settings, IDraftModel claudeCode, IDraftModel? apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(claudeCode);
        _settings = settings;
        _claudeCode = claudeCode;
        _apiKey = apiKey;
    }

    public Task<ModelStatus> CheckAsync(CancellationToken ct)
    {
        if (_settings().Backend == AiBackend.ApiKey)
            return _apiKey is null
                ? Task.FromResult(new ModelStatus(false, ApiKeyUnavailableText))
                : _apiKey.CheckAsync(ct);
        return _claudeCode.CheckAsync(ct);
    }

    public Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_settings().Backend == AiBackend.ApiKey)
            return _apiKey is null
                ? Task.FromResult<ModelResult>(new ModelResult.Failed(ModelFailure.NotInstalled, null))
                : _apiKey.RunAsync(request, ct);
        return _claudeCode.RunAsync(request, ct);
    }
}
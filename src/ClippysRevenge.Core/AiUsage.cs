using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>Sums over calls. Null token values count as 0. UnknownCalls = calls with Input and Output both null (SPEC 19.13).</summary>
public sealed record AiUsageTotals(int Calls, int UnknownCalls, long Input, long Output, long CacheRead, long CacheCreation)
{
    public static AiUsageTotals Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public long In => Input + CacheRead + CacheCreation;
    public long Out => Output;
    public long Total => In + Out;

    /// <summary>One more call. usage null or !Known counts as unknown; its non-null fields are still summed.</summary>
    public AiUsageTotals Add(TokenUsage? usage) => new(
        Calls + 1,
        UnknownCalls + (usage is { Known: true } ? 0 : 1),
        Input + (usage?.Input ?? 0),
        Output + (usage?.Output ?? 0),
        CacheRead + (usage?.CacheRead ?? 0),
        CacheCreation + (usage?.CacheCreation ?? 0));

    /// <summary>Add(UsageRecordingModel.UsageOf(result)) when UsageRecordingModel.IsRecorded(result); else this.</summary>
    /// <exception cref="ArgumentNullException">result is null.</exception>
    public AiUsageTotals AddCall(ModelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return UsageRecordingModel.IsRecorded(result) ? Add(UsageRecordingModel.UsageOf(result)) : this;
    }
}

/// <summary>Today's totals in memory, backed by the own index's ai_calls (SPEC 19.13). Thread-safe.</summary>
public sealed class AiUsage
{
    private readonly Func<EmailIndex> _ownIndex;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private DateOnly _day;
    private AiUsageTotals _today = AiUsageTotals.Zero;

    /// <param name="ownIndex">() =&gt; indexes.For(MailboxIndexes.OwnMailbox). Called on each Load/Record.</param>
    /// <exception cref="ArgumentNullException">ownIndex or zone is null.</exception>
    public AiUsage(Func<EmailIndex> ownIndex, TimeZoneInfo zone, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ownIndex);
        ArgumentNullException.ThrowIfNull(zone);
        _ownIndex = ownIndex;
        _zone = zone;
        _time = time ?? TimeProvider.System;
        _log = logger;
        _day = LocalDay(_time.GetUtcNow());
    }

    /// <summary>Sets today's totals to ownIndex().AiUsageSince(local midnight). Never throws (Warning, type only;
    /// totals stay Zero).</summary>
    public void Load()
    {
        var now = _time.GetUtcNow();
        AiUsageTotals totals;
        try
        {
            totals = _ownIndex().AiUsageSince(LocalMidnightUtc(now, _zone));
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI usage: couldn't load today's calls ({Type}).", ex.GetType().Name);
            totals = AiUsageTotals.Zero;
        }
        lock (_lock)
        {
            _day = LocalDay(now);
            _today = totals;
        }
    }

    /// <summary>at = time.GetUtcNow(). Inserts one row (AddAiCall), then adds to today's totals. The in-memory
    /// add happens even when the insert fails (Warning, type only). Never throws.</summary>
    public void Record(ModelCallKind kind, AiBackend backend, bool ok, TokenUsage? usage)
    {
        try
        {
            var at = _time.GetUtcNow();
            try
            {
                _ownIndex().AddAiCall(at, kind, BackendName(backend), ok, usage);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI usage: couldn't record a call ({Type}).", ex.GetType().Name);
            }
            lock (_lock)
            {
                RollOver(at);
                _today = _today.Add(usage);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI usage: couldn't record a call ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Today's totals. The first read (or Record) after local midnight resets them to Zero first.</summary>
    public AiUsageTotals Today()
    {
        lock (_lock)
        {
            RollOver(_time.GetUtcNow());
            return _today;
        }
    }

    /// <summary>"cli" for ClaudeCode, "api" for ApiKey.</summary>
    /// <exception cref="ArgumentOutOfRangeException">backend isn't defined.</exception>
    public static string BackendName(AiBackend backend) => backend switch
    {
        AiBackend.ClaudeCode => "cli",
        AiBackend.ApiKey => "api",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    /// <summary>Local midnight of now's local day, as UTC. An invalid local midnight (DST gap) moves 1 h later
    /// (the DraftWorker.LocalMidnightUtc rule, copied; DraftWorker is not refactored).</summary>
    internal static DateTimeOffset LocalMidnightUtc(DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = localNow.Date;
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private DateOnly LocalDay(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).Date);

    // Caller holds _lock.
    private void RollOver(DateTimeOffset now)
    {
        var day = LocalDay(now);
        if (day == _day) return;
        _day = day;
        _today = AiUsageTotals.Zero;
    }
}

/// <summary>Wraps the DraftModelSwitch in Program (SPEC 19.13).</summary>
public sealed class UsageRecordingModel : IDraftModel
{
    private readonly IDraftModel _inner;
    private readonly Func<AiDraftingSettings> _settings;
    private readonly AiUsage _usage;
    private readonly ILogger? _log;

    /// <exception cref="ArgumentNullException">inner, settings or usage is null.</exception>
    public UsageRecordingModel(IDraftModel inner, Func<AiDraftingSettings> settings, AiUsage usage, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);
        _inner = inner;
        _settings = settings;
        _usage = usage;
        _log = logger;
    }

    /// <summary>inner.CheckAsync, nothing recorded.</summary>
    public Task<ModelStatus> CheckAsync(CancellationToken ct) => _inner.CheckAsync(ct);

    /// <summary>Reads settings().Backend, awaits inner.RunAsync, records when IsRecorded, returns the inner result
    /// unchanged (same instance). Exceptions from inner propagate unrecorded.</summary>
    public async Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiBackend? backend = null;
        try
        {
            backend = _settings().Backend;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI usage: couldn't read the backend ({Type}).", ex.GetType().Name);
        }

        var result = await _inner.RunAsync(request, ct).ConfigureAwait(false);

        if (backend is { } b && IsRecorded(result))
        {
            try
            {
                _usage.Record(request.Kind, b, result is ModelResult.Ok, UsageOf(result));
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI usage: couldn't record a call ({Type}).", ex.GetType().Name);
            }
        }
        return result;
    }

    /// <summary>True for Ok, and Failed with Kind Timeout, BadOutput, NoStructuredOutput, ProcessExit or Http.
    /// False for RateLimited, NotSignedIn, Failed NotInstalled/BadInput/Cancelled, null and anything else.</summary>
    public static bool IsRecorded(ModelResult? result) => result switch
    {
        ModelResult.Ok => true,
        ModelResult.Failed f => f.Kind is ModelFailure.Timeout or ModelFailure.BadOutput
            or ModelFailure.NoStructuredOutput or ModelFailure.ProcessExit or ModelFailure.Http,
        _ => false,
    };

    /// <summary>Ok.Usage or Failed.Usage; else null.</summary>
    public static TokenUsage? UsageOf(ModelResult? result) => result switch
    {
        ModelResult.Ok ok => ok.Usage,
        ModelResult.Failed f => f.Usage,
        _ => null,
    };
}

public static class AiUsageTexts
{
    /// <summary>SPEC 19.13 forms.</summary>
    /// <exception cref="ArgumentNullException">t is null.</exception>
    public static string Today(AiUsageTotals t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var ci = CultureInfo.InvariantCulture;
        if (t.Calls == 0) return "Today: no AI calls";
        var calls = t.Calls.ToString("N0", ci) + (t.Calls == 1 ? " AI call" : " AI calls");
        if (t.UnknownCalls >= t.Calls) return $"Today: {calls}, tokens unknown";
        var text = $"Today: {calls}, ~{TokenCount.Format(t.Total)} tokens (in {TokenCount.Format(t.In)} / out {TokenCount.Format(t.Out)})";
        if (t.UnknownCalls > 0) text += $", {t.UnknownCalls.ToString("N0", ci)} without token data";
        return text;
    }

    /// <summary>The tray/Activity line: null when ai is null, !ai.HasConsent or usage is null; else Today(usage.Today()).
    /// Never throws (any exception gives null).</summary>
    public static string? Line(AiDraftingSettings? ai, AiUsage? usage)
    {
        try
        {
            if (ai is null || !ai.HasConsent || usage is null) return null;
            return Today(usage.Today());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"" when unknownCalls &lt;= 0, else ", &lt;u&gt; calls without token data" (invariant).</summary>
    public static string UnknownCallsLogDetail(int unknownCalls) =>
        unknownCalls <= 0 ? "" : ", " + unknownCalls.ToString(CultureInfo.InvariantCulture) + " calls without token data";
}

public static class TokenCount
{
    /// <summary>SPEC 19.13 short form. Negative n formats as "0".</summary>
    public static string Format(long n)
    {
        var ci = CultureInfo.InvariantCulture;
        if (n < 0) n = 0;
        if (n < 1_000) return n.ToString(ci);

        var k1 = Math.Round(n / 1000m, 1, MidpointRounding.AwayFromZero);
        if (n < 100_000 && k1 < 100) return DropZero(k1.ToString("0.0", ci)) + "k";

        var k0 = Math.Round(n / 1000m, 0, MidpointRounding.AwayFromZero);
        if (n < 1_000_000 && k0 < 1000) return k0.ToString("0", ci) + "k";

        var m1 = Math.Round(n / 1_000_000m, 1, MidpointRounding.AwayFromZero);
        return DropZero(m1.ToString("0.0", ci)) + "M";
    }

    private static string DropZero(string s) => s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s;
}

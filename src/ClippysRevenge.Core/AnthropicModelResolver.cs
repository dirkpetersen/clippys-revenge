using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>One /v1/models entry, read from the SDK item's RawData (never from typed getters).</summary>
public sealed record ModelListEntry(string Id, DateTimeOffset CreatedAt, string? Line, string? Lifecycle)
{
    /// <summary>False when "id" isn't a non-empty string or "created_at" isn't a string that parses as an
    /// RFC 3339 date (DateTimeOffset.TryParse, InvariantCulture, DateTimeStyles.RoundtripKind | AssumeUniversal).
    /// "line"/"lifecycle": the string value, or null when missing, JSON null or not a string. Never throws.</summary>
    public static bool TryParse(IReadOnlyDictionary<string, JsonElement>? raw, [NotNullWhen(true)] out ModelListEntry? entry)
    {
        entry = null;
        try
        {
            if (raw is null) return false;
            var id = Str(raw, "id");
            if (string.IsNullOrEmpty(id)) return false;
            var created = Str(raw, "created_at");
            if (created is null
                || !DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var at))
                return false;
            entry = new ModelListEntry(id, at, Str(raw, "line"), Str(raw, "lifecycle"));
            return true;
        }
        catch (Exception)
        {
            entry = null;
            return false;
        }
    }

    private static string? Str(IReadOnlyDictionary<string, JsonElement> raw, string name) =>
        raw.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What one listing attempt gave (closed hierarchy, private constructor).</summary>
public abstract record ModelListOutcome
{
    private ModelListOutcome() { }
    public sealed record Listed(IReadOnlyList<ModelListEntry> Models) : ModelListOutcome;
    /// <summary>HTTP 401 or 403.</summary>
    public sealed record Rejected(int HttpStatus) : ModelListOutcome;
    /// <summary>Kind is a <see cref="ModelLookupKinds"/> text.</summary>
    public sealed record Failed(string Kind) : ModelListOutcome;
}

public static class ModelLookupKinds
{
    /// <summary>Network/IO error, any other exception.</summary>
    public const string NoAnswer = "no answer";
    /// <summary>The 15 s bound, not the caller's ct.</summary>
    public const string TimedOut = "timed out";
    /// <summary>JsonException / AnthropicInvalidDataException.</summary>
    public const string Unreadable = "unreadable answer";
    /// <summary>Listed fine, SelectNewest gave null.</summary>
    public const string NoMatch = "no model of that family";
    public static string Http(int status) => "HTTP " + status.ToString(CultureInfo.InvariantCulture);
}

public enum ModelResolutionKind { Resolved, Fallback, Rejected }

/// <summary>Resolved: Model is the typed non-alias or the looked-up/cached id.
/// Fallback: Model is ModelAliases.Fallback(alias). Rejected: Model is "", HttpStatus is 401/403.</summary>
public sealed record ModelResolution(string Model, ModelResolutionKind Kind, int? HttpStatus = null);

/// <summary>SPEC 19.3 "Model aliases", API backend. Thread-safe.</summary>
public sealed class AnthropicModelResolver
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    public const int MaxPages = 10;
    public const int PageSize = 100;

    private readonly Func<string, CancellationToken, Task<ModelListOutcome>> _lister;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _flight = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Id, DateTimeOffset StoredAt)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _holds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _episodes = new(StringComparer.Ordinal);
    private int _generation;

    /// <param name="lister">(apiKey, ct) -> one listing attempt. Must throw only OperationCanceledException for ct.</param>
    /// <exception cref="ArgumentNullException">lister is null.</exception>
    public AnthropicModelResolver(Func<string, CancellationToken, Task<ModelListOutcome>> lister,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _time = time ?? TimeProvider.System;
        _log = logger;
    }

    /// <summary>Non-alias: Resolved(model) at once, no call, no log.
    /// Alias: cache, hold, then one lookup. fresh = true (CheckAsync) skips the cache and the 10-minute hold.</summary>
    /// <exception cref="ArgumentNullException">model or apiKey is null.</exception>
    /// <exception cref="OperationCanceledException">ct was cancelled (cache/hold unchanged).</exception>
    public async Task<ModelResolution> ResolveAsync(string model, string apiKey, bool fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (!ModelAliases.IsAlias(model)) return new ModelResolution(model, ModelResolutionKind.Resolved);

        if (!fresh && TryAnswerWithoutCall(model, out var quick)) return quick;

        await _flight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!fresh && TryAnswerWithoutCall(model, out var again)) return again;

            int generation;
            lock (_lock) generation = _generation;

            ModelListOutcome outcome;
            try
            {
                outcome = await _lister(apiKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                outcome = new ModelListOutcome.Failed(ModelLookupKinds.NoAnswer);
            }

            if (outcome is ModelListOutcome.Rejected rejected)
                return new ModelResolution("", ModelResolutionKind.Rejected, rejected.HttpStatus);

            string failure;
            if (outcome is ModelListOutcome.Listed listed)
            {
                var id = SelectNewest(listed.Models, model);
                if (id is not null)
                {
                    var now = _time.GetUtcNow();
                    lock (_lock)
                    {
                        if (generation == _generation)
                        {
                            _cache[model] = (id, now);
                            _holds.Remove(model);
                            _episodes.Remove(model);
                        }
                    }
                    _log?.LogInformation("AI drafts: \"{Alias}\" is {Model}.", model, id);
                    return new ModelResolution(id, ModelResolutionKind.Resolved);
                }
                failure = ModelLookupKinds.NoMatch;
            }
            else
            {
                failure = ((ModelListOutcome.Failed)outcome).Kind;
            }

            var fallback = ModelAliases.Fallback(model);
            bool warn;
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _holds[model] = _time.GetUtcNow() + RetryAfterFailure;
                    if (fresh) _cache.Remove(model);
                    warn = _episodes.Add(model);
                }
                else
                {
                    warn = true;
                }
            }
            if (warn)
                _log?.LogWarning("AI drafts: couldn't look up the newest \"{Alias}\" model ({Kind}); using {Model}.",
                    model, failure, fallback);
            return new ModelResolution(fallback, ModelResolutionKind.Fallback);
        }
        finally
        {
            _flight.Release();
        }
    }

    private bool TryAnswerWithoutCall(string alias, out ModelResolution resolution)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_cache.TryGetValue(alias, out var c) && now - c.StoredAt < CacheFor)
            {
                resolution = new ModelResolution(c.Id, ModelResolutionKind.Resolved);
                return true;
            }
            if (_holds.TryGetValue(alias, out var until) && now < until)
            {
                resolution = new ModelResolution(ModelAliases.Fallback(alias), ModelResolutionKind.Fallback);
                return true;
            }
        }
        resolution = null!;
        return false;
    }

    /// <summary>Drops every cached id, every hold and every open warning episode. A lookup already running
    /// when this is called doesn't store its result (generation counter). Never throws.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _cache.Clear();
            _holds.Clear();
            _episodes.Clear();
        }
    }

    /// <summary>The entry with <c>Line == alias</c> (ordinal), not retired, with a valid id, that has the latest
    /// CreatedAt (ties: the ordinal-greatest id). Null when none qualifies.</summary>
    /// <exception cref="ArgumentNullException">models or alias is null.</exception>
    public static string? SelectNewest(IEnumerable<ModelListEntry> models, string alias)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(alias);
        ModelListEntry? best = null;
        foreach (var m in models)
        {
            if (m is null
                || !string.Equals(m.Line, alias, StringComparison.Ordinal)
                || string.Equals(m.Lifecycle, "retired", StringComparison.Ordinal)
                || !AiDraftingSettings.IsValidModelName(m.Id)) continue;
            if (best is null
                || m.CreatedAt > best.CreatedAt
                || (m.CreatedAt == best.CreatedAt && string.CompareOrdinal(m.Id, best.Id) > 0))
                best = m;
        }
        return best?.Id;
    }
}

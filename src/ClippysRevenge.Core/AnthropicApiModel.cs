using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Microsoft.Extensions.Logging;
using Sdk = Anthropic.Models.Messages;

namespace ClippysRevenge.Core;

/// <summary>The optional IDraftModel over the official Anthropic C# SDK (SPEC 19.3). Thread-safe.</summary>
public sealed class AnthropicApiModel : IDraftModel, IDisposable
{
    public const string ApiBaseUrl = "https://api.anthropic.com";
    /// <summary>Whole-check bound for GET /v1/models/{model}.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Added to our own deadline for the SDK's per-attempt timeout, so ours always fires first.</summary>
    public static readonly TimeSpan SdkTimeoutSlack = TimeSpan.FromSeconds(60);
    public const int MaxOutputTokensLimit = 32_000;
    /// <summary>RetryAt beyond now + this is dropped (null).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(8);

    private readonly Func<AiDraftingSettings> _settings;
    private readonly Func<string?> _loadApiKey;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private int _disposed;

    /// <param name="settings">Read by CheckAsync for Model only. RunAsync uses request.Model/Effort.</param>
    /// <param name="loadApiKey">Called once per RunAsync/CheckAsync; App passes SettingsStore.LoadApiKey.
    /// Null, a throw, or a value failing SettingsStore.IsValidApiKeyFormat all mean "no key".</param>
    /// <param name="handler">Innermost HTTP handler (tests pass a fake). Null = an owned SocketsHttpHandler.
    /// A passed handler is never disposed by this class.</param>
    /// <exception cref="ArgumentNullException">settings or loadApiKey is null.</exception>
    public AnthropicApiModel(Func<AiDraftingSettings> settings, Func<string?> loadApiKey,
        HttpMessageHandler? handler = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loadApiKey = loadApiKey ?? throw new ArgumentNullException(nameof(loadApiKey));
        if (handler is null)
        {
            _handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
            };
            _ownsHandler = true;
        }
        else
        {
            _handler = handler;
        }
        _time = time ?? TimeProvider.System;
        _log = logger;
    }

    /// <summary>Disposes the owned SocketsHttpHandler, if any. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHandler) _handler.Dispose();
    }

    /// <summary>Reads the key per call; null for no usable key. Never throws.</summary>
    private string? SafeLoadKey()
    {
        try
        {
            var key = _loadApiKey()?.Trim();
            if (string.IsNullOrEmpty(key) || !SettingsStore.IsValidApiKeyFormat(key)) return null;
            return key;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AnthropicClient NewClient(string key, RetryAfterCapture capture, TimeSpan deadline) =>
        new AnthropicClient(new ClientOptions
        {
            ApiKey = key,
            HttpClient = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            Handlers = [capture],
            BaseUrl = ApiBaseUrl,
            MaxRetries = 0,
            Timeout = deadline + SdkTimeoutSlack,
            OpenTelemetry = false,
        });

    // ---- CheckAsync ----

    public async Task<ModelStatus> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (status, kind, http) = await CheckCoreAsync(ct).ConfigureAwait(false);
        _log?.LogInformation("Anthropic API status: {Kind}, HTTP {Status}", kind, http);
        return status;
    }

    private async Task<(ModelStatus, string, int?)> CheckCoreAsync(CancellationToken ct)
    {
        var model = _settings().Model;
        if (!ClaudeCliArgs.IsValidModel(model))
            return (new ModelStatus(false, AnthropicApiStatusTexts.ModelUnavailable(model)), "ModelUnavailable", null);

        var key = SafeLoadKey();
        if (key is null) return (new ModelStatus(false, AnthropicApiStatusTexts.NoKey), "NoKey", null);

        var capture = new RetryAfterCapture();
        using var client = NewClient(key, capture, CheckTimeout);
        using var deadlineCts = new CancellationTokenSource(CheckTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        try
        {
            using var resp = await client.WithRawResponse.Models.Retrieve(model, cancellationToken: linked.Token)
                .ConfigureAwait(false);
            return (new ModelStatus(true, AnthropicApiStatusTexts.Ready(model)), "Ready", 200);
        }
        catch (AnthropicApiException e)
        {
            var code = (int)e.StatusCode;
            return code switch
            {
                401 or 403 => (new ModelStatus(false, AnthropicApiStatusTexts.Rejected(code)), "Rejected", code),
                400 or 404 => (new ModelStatus(false, AnthropicApiStatusTexts.ModelUnavailable(model)), "ModelUnavailable", code),
                _ => (new ModelStatus(false, AnthropicApiStatusTexts.NoAnswer), "NoAnswer", code),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception)
        {
            return (new ModelStatus(false, AnthropicApiStatusTexts.NoAnswer), "NoAnswer", null);
        }
    }

    // ---- RunAsync ----

    public async Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = _time.GetTimestamp();
        var (result, http) = await RunCoreAsync(request, ct).ConfigureAwait(false);

        int? inTok = null, outTok = null;
        string kind;
        switch (result)
        {
            case ModelResult.Ok ok: kind = "Ok"; inTok = ok.InputTokens; outTok = ok.OutputTokens; break;
            case ModelResult.RateLimited: kind = "RateLimited"; break;
            case ModelResult.NotSignedIn: kind = "NotSignedIn"; break;
            case ModelResult.Failed f: kind = "Failed:" + f.Kind; break;
            default: kind = "Unknown"; break;
        }
        _log?.LogInformation("Anthropic API call: {ResultKind} in {Ms} ms, tokens {In}/{Out}, HTTP {Status}",
            kind, (long)_time.GetElapsedTime(start).TotalMilliseconds, inTok, outTok, http);
        return result;
    }

    private static ModelResult Bad(ModelFailure kind) => new ModelResult.Failed(kind, null);

    private async Task<(ModelResult, int?)> RunCoreAsync(ModelRequest request, CancellationToken ct)
    {
        if (request.Timeout <= TimeSpan.Zero
            || request.SystemPrompt is null
            || string.IsNullOrEmpty(request.UserPrompt)
            || Encoding.UTF8.GetByteCount(request.UserPrompt) > ClaudeCliRunner.MaxPromptBytes
            || !ClaudeCliArgs.IsValidModel(request.Model)
            || !ClaudeCliArgs.IsValidEffort(request.Effort)
            || request.MaxOutputTokens is < 1 or > MaxOutputTokensLimit
            || !AnthropicWireSchema.TryReduce(request.JsonSchema, out var schema))
            return (Bad(ModelFailure.BadInput), null);

        if (ct.IsCancellationRequested) return (Bad(ModelFailure.Cancelled), null);

        var key = SafeLoadKey();
        if (key is null) return (new ModelResult.NotSignedIn(), null);

        var p = new Sdk.MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Messages = [new Sdk.UserMessageParam(request.UserPrompt)],
            OutputConfig = new Sdk.OutputConfig
            {
                Effort = request.Effort,
                Format = new Sdk.JsonOutputFormat { Schema = schema },
            },
        };
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) p = p with { System = request.SystemPrompt };

        var capture = new RetryAfterCapture();
        using var client = NewClient(key, capture, request.Timeout);
        using var deadlineCts = new CancellationTokenSource(request.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        var start = _time.GetTimestamp();
        try
        {
            var msg = await client.Messages.Create(p, linked.Token).ConfigureAwait(false);
            var duration = _time.GetElapsedTime(start);

            var stop = msg.StopReason?.Raw();
            var text = new StringBuilder();
            foreach (var block in msg.Content)
            {
                if (block.TryPickText(out var t)) text.Append(t.Text);
            }

            int? inTok = null, outTok = null;
            try
            {
                inTok = Tok(msg.Usage.InputTokens);
                outTok = Tok(msg.Usage.OutputTokens);
            }
            catch (Exception)
            {
                inTok = outTok = null;
            }

            if (stop == "refusal") return (Bad(ModelFailure.NoStructuredOutput), 200);
            if (stop == "max_tokens") return (Bad(ModelFailure.BadOutput), 200);

            JsonElement output;
            try
            {
                using var doc = JsonDocument.Parse(text.ToString().Trim());
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (Bad(ModelFailure.NoStructuredOutput), 200);
                output = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return (Bad(ModelFailure.NoStructuredOutput), 200);
            }
            return (new ModelResult.Ok(output, duration, inTok, outTok), 200);
        }
        catch (AnthropicApiException e)
        {
            var code = (int)e.StatusCode;
            return code switch
            {
                401 or 403 => (new ModelResult.NotSignedIn(), code),
                429 or 529 => (new ModelResult.RateLimited(capture.RetryAt(_time.GetUtcNow())), code),
                _ => (new ModelResult.Failed(ModelFailure.Http, code), code),
            };
        }
        catch (OperationCanceledException)
        {
            return (Bad(ct.IsCancellationRequested ? ModelFailure.Cancelled : ModelFailure.Timeout), null);
        }
        catch (Exception e) when (e is JsonException or AnthropicInvalidDataException)
        {
            return (Bad(ModelFailure.BadOutput), 200);
        }
        catch (Exception)
        {
            // AnthropicIOException, HttpRequestException and anything else. Never read the message.
            return (Bad(ModelFailure.Http), null);
        }
    }

    private static int? Tok(long v) => v is >= 0 and <= int.MaxValue ? (int)v : null;

    /// <summary>Records retry-after from the response. One instance per call.</summary>
    private sealed class RetryAfterCapture : DelegatingHandler
    {
        private TimeSpan? _delta;
        private DateTimeOffset? _date;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            RetryConditionHeaderValue? ra = resp.Headers.RetryAfter;
            _delta = ra?.Delta;
            _date = ra?.Date;
            return resp;
        }

        public DateTimeOffset? RetryAt(DateTimeOffset now)
        {
            DateTimeOffset? at = null;
            if (_delta is { } d && d > TimeSpan.Zero) at = now + d;
            else if (_date is { } dt && dt > now) at = dt;
            if (at is { } a && a > now + MaxRetryAfter) return null;
            return at;
        }
    }
}

public static class AnthropicApiStatusTexts
{
    public const string NoKey = "\u2717 No API key saved.";
    public const string NoAnswer = "\u2717 api.anthropic.com didn't answer.";
    public static string Ready(string model) => $"\u2713 API key works ({model})";
    public static string Rejected(int httpStatus) => $"\u2717 The API key was rejected (HTTP {httpStatus}).";
    public static string ModelUnavailable(string model) => $"\u2717 The model \"{model}\" isn't available with this key.";
}

/// <summary>Reduces a JSON schema to what structured outputs accept (SPEC 19.3). Pure.</summary>
public static class AnthropicWireSchema
{
    private static readonly string[] Dropped =
        ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "maxItems"];

    private static readonly HashSet<string> SchemaMaps = new(StringComparer.Ordinal)
        { "properties", "patternProperties", "$defs", "definitions" };

    private static readonly HashSet<string> SchemaValues = new(StringComparer.Ordinal)
        { "items", "additionalItems", "not", "if", "then", "else", "contains" };

    private static readonly HashSet<string> SchemaLists = new(StringComparer.Ordinal)
        { "anyOf", "allOf", "oneOf", "prefixItems" };

    /// <summary>Keywords removed from every schema object (see Behavior B).</summary>
    public static IReadOnlyList<string> DroppedKeywords { get; } = Dropped;

    /// <summary>False when jsonSchema isn't a JSON object. Never throws.
    /// The dictionary is the reduced root object's members (values Clone()d), in document order.</summary>
    public static bool TryReduce(string? jsonSchema, out IReadOnlyDictionary<string, JsonElement> schema)
    {
        schema = new Dictionary<string, JsonElement>();
        if (string.IsNullOrWhiteSpace(jsonSchema)) return false;
        try
        {
            using var input = JsonDocument.Parse(jsonSchema);
            if (input.RootElement.ValueKind != JsonValueKind.Object) return false;

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                WriteSchema(w, input.RootElement);
            }
            using var reduced = JsonDocument.Parse(ms.ToArray());
            var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in reduced.RootElement.EnumerateObject()) d[prop.Name] = prop.Value.Clone();
            schema = d.AsReadOnly();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteSchema(Utf8JsonWriter w, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            e.WriteTo(w);
            return;
        }
        w.WriteStartObject();
        foreach (var prop in e.EnumerateObject())
        {
            var name = prop.Name;
            if (Array.IndexOf(Dropped, name) >= 0) continue;
            if (name == "minItems" && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetDouble(out var min) && min > 1) continue;

            w.WritePropertyName(name);
            if (SchemaMaps.Contains(name) && prop.Value.ValueKind == JsonValueKind.Object)
            {
                w.WriteStartObject();
                foreach (var member in prop.Value.EnumerateObject())
                {
                    w.WritePropertyName(member.Name);
                    WriteSchema(w, member.Value);
                }
                w.WriteEndObject();
            }
            else if (SchemaValues.Contains(name) && prop.Value.ValueKind == JsonValueKind.Object)
            {
                WriteSchema(w, prop.Value);
            }
            else if (SchemaLists.Contains(name) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                w.WriteStartArray();
                foreach (var item in prop.Value.EnumerateArray()) WriteSchema(w, item);
                w.WriteEndArray();
            }
            else
            {
                prop.Value.WriteTo(w);
            }
        }
        w.WriteEndObject();
    }
}

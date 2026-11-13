using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>What to start: an executable plus arguments that go before the CLI arguments (e.g. node.exe + cli.js).</summary>
public sealed record ClaudeCliCommand(string FileName, IReadOnlyList<string> PrefixArgs);

/// <summary>Finds the user's installed Claude Code CLI (SPEC 19.3). Never runs anything.</summary>
public sealed class ClaudeCliLocator
{
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isWindows;

    /// <param name="getEnv">Environment lookup (PATH, USERPROFILE, HOME, APPDATA).</param>
    /// <param name="fileExists">File check (tests pass a set).</param>
    /// <param name="isWindows">Path rules and file names; tests pass true on Linux.</param>
    public ClaudeCliLocator(Func<string, string?> getEnv, Func<string, bool> fileExists, bool isWindows)
    {
        _getEnv = getEnv ?? throw new ArgumentNullException(nameof(getEnv));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _isWindows = isWindows;
    }

    /// <summary>Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows().</summary>
    public static ClaudeCliLocator Default { get; } =
        new(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    private char Sep => _isWindows ? '\\' : '/';
    private string ClaudeName => _isWindows ? "claude.exe" : "claude";
    private string NodeName => _isWindows ? "node.exe" : "node";

    /// <summary>Null = not found. Never throws.</summary>
    public ClaudeCliCommand? Find(string? configuredPath)
    {
        try
        {
            return FindCore(configuredPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ClaudeCliCommand? FindCore(string? configuredPath)
    {
        var configured = configuredPath?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            if (!IsFullyQualified(configured)) return null;
            var ext = ExtensionOf(configured);
            if (ext is ".cmd" or ".bat" or ".ps1") return null;
            if (_isWindows && ext is not (".exe" or ".js")) return null;
            if (!Exists(configured)) return null;
            if (ext == ".js")
            {
                var node = FindOnPath(NodeName);
                return node is null ? null : new ClaudeCliCommand(node, [configured]);
            }
            return new ClaudeCliCommand(configured, []);
        }

        var onPath = FindOnPath(ClaudeName);
        if (onPath is not null) return new ClaudeCliCommand(onPath, []);

        var home = _getEnv(_isWindows ? "USERPROFILE" : "HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var native = Join(home, ".local", "bin", ClaudeName);
            if (IsFullyQualified(native) && Exists(native)) return new ClaudeCliCommand(native, []);
        }

        if (_isWindows)
        {
            var appData = _getEnv("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                var cliJs = Join(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
                if (IsFullyQualified(cliJs) && Exists(cliJs))
                {
                    var node = FindOnPath(NodeName);
                    if (node is not null) return new ClaudeCliCommand(node, [cliJs]);
                }
            }
        }
        return null;
    }

    private string? FindOnPath(string exeName)
    {
        var path = _getEnv("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var raw in path.Split(_isWindows ? ';' : ':'))
        {
            var dir = raw.Trim();
            if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"') dir = dir[1..^1];
            if (dir.Length == 0 || !IsFullyQualified(dir)) continue;
            var candidate = Join(dir, exeName);
            if (Exists(candidate)) return candidate;
        }
        return null;
    }

    private bool Exists(string path)
    {
        try
        {
            return _fileExists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string Join(string dir, params string[] parts)
    {
        var sb = new StringBuilder(dir.TrimEnd(Sep));
        foreach (var p in parts) sb.Append(Sep).Append(p);
        return sb.ToString();
    }

    private bool IsFullyQualified(string p)
    {
        if (_isWindows)
            return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\' && !p.Contains('/');
        return p.StartsWith('/');
    }

    private string ExtensionOf(string p)
    {
        var lastSep = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        var dot = p.LastIndexOf('.');
        return dot > lastSep && dot >= 0 ? p[dot..].ToLowerInvariant() : "";
    }
}

/// <summary>The only place that builds Claude CLI arguments (SPEC 19.3).</summary>
public static class ClaudeCliArgs
{
    public static IReadOnlyList<string> Version { get; } = ["--version"];
    public static IReadOnlyList<string> Help { get; } = ["--help"];
    public static IReadOnlyList<string> AuthStatus { get; } = ["auth", "status", "--json"];

    /// <summary>Flags `--help` must mention (SPEC 19.3 step 2), in this order.</summary>
    public static IReadOnlyList<string> RequiredHelpFlags { get; } =
        ["--json-schema", "--no-session-persistence", "--strict-mcp-config", "--tools", "--effort", "--system-prompt"];

    public const string SafeModeFlag = "--safe-mode";

    /// <summary>True when helpText lists flag as a whole token.</summary>
    public static bool HelpMentions(string helpText, string flag)
    {
        if (string.IsNullOrEmpty(helpText) || string.IsNullOrEmpty(flag)) return false;
        try
        {
            return Regex.IsMatch(helpText, "(?<![A-Za-z0-9_-])" + Regex.Escape(flag) + "(?![A-Za-z0-9_-])",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    internal static bool IsValidModel(string? model) => AiDraftingSettings.IsValidModelName(model);

    internal static bool IsValidEffort(string? effort) =>
        effort is not null && AiDraftingSettings.Efforts.Contains(effort, StringComparer.Ordinal);

    /// <summary>Compact re-serialization of a JSON object schema; false when it isn't a JSON object.</summary>
    internal static bool TryCompactSchema(string? schema, out string compact)
    {
        compact = "";
        if (string.IsNullOrWhiteSpace(schema)) return false;
        try
        {
            using var doc = JsonDocument.Parse(schema);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                doc.RootElement.WriteTo(w);
            }
            compact = Encoding.UTF8.GetString(ms.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The SPEC 19.3 list, one element per argument.</summary>
    /// <exception cref="ArgumentException">An invalid argument; the message names the parameter only.</exception>
    public static IReadOnlyList<string> Build(string model, string effort, string jsonSchema, string systemPromptFile, bool safeMode)
    {
        if (!IsValidModel(model)) throw new ArgumentException("Invalid model name.", nameof(model));
        if (!IsValidEffort(effort)) throw new ArgumentException("Invalid effort.", nameof(effort));
        if (!TryCompactSchema(jsonSchema, out var schema))
            throw new ArgumentException("Not a JSON object.", nameof(jsonSchema));
        if (string.IsNullOrEmpty(systemPromptFile) || !Path.IsPathFullyQualified(systemPromptFile))
            throw new ArgumentException("Not a fully qualified path.", nameof(systemPromptFile));

        var list = new List<string>
        {
            "-p",
            "--model", model,
            "--effort", effort,
            "--output-format", "json",
            "--json-schema", schema,
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--no-session-persistence",
            "--system-prompt-file", systemPromptFile,
            "--disable-slash-commands",
        };
        if (safeMode) list.Add(SafeModeFlag);
        return list;
    }
}

public enum CliRunOutcome { Exited, NotStarted, SetupFailed, PromptTooLarge, TimedOut, Cancelled, OutputTooLarge }

/// <summary>ExitCode is set only for Exited. Stdout/Stderr are "" unless Exited. Never logged.</summary>
public sealed record CliRunResult(CliRunOutcome Outcome, int? ExitCode, string Stdout, string Stderr, TimeSpan Duration);

public interface IClaudeCliRunner
{
    Task<CliRunResult> RunAsync(ClaudeCliCommand command, IReadOnlyList<string> args, string? stdin,
        TimeSpan timeout, CancellationToken ct);

    /// <summary>System-prompt file + ClaudeCliArgs.Build + RunAsync with the user prompt on stdin.</summary>
    Task<CliRunResult> RunPromptAsync(ClaudeCliCommand command, ModelRequest request, bool safeMode, CancellationToken ct);
}

/// <summary>Runs the CLI as a child process (no shell). The only Core code that starts a process.</summary>
public sealed class ClaudeCliRunner : IClaudeCliRunner
{
    public const string WorkDirName = "ai";
    public const int MaxPromptBytes = 2 * 1024 * 1024;
    public const int MaxStdoutChars = 4 * 1024 * 1024;
    public const int MaxStderrChars = 64 * 1024;
    public static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly string _workDir;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private int _cleaned;

    /// <param name="workDir">Fully qualified; created on demand.</param>
    public ClaudeCliRunner(string workDir, TimeProvider? time = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Path.IsPathFullyQualified(workDir))
            throw new ArgumentException("Must be a fully qualified path.", nameof(workDir));
        _workDir = workDir;
        _time = time ?? TimeProvider.System;
        _log = logger;
    }

    public async Task<CliRunResult> RunAsync(ClaudeCliCommand command, IReadOnlyList<string> args, string? stdin,
        TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var start = _time.GetTimestamp();
        var result = await RunCoreAsync(command, args, stdin, timeout, ct, start).ConfigureAwait(false);
        _log?.LogDebug("Claude CLI process: {Outcome} exit {ExitCode} in {Ms} ms",
            result.Outcome, result.ExitCode, (long)result.Duration.TotalMilliseconds);
        return result;
    }

    private async Task<CliRunResult> RunCoreAsync(ClaudeCliCommand command, IReadOnlyList<string> args, string? stdin,
        TimeSpan timeout, CancellationToken ct, long start)
    {
        CliRunResult Plain(CliRunOutcome o) => new(o, null, "", "", _time.GetElapsedTime(start));

        try
        {
            Directory.CreateDirectory(_workDir);
        }
        catch (Exception)
        {
            return Plain(CliRunOutcome.SetupFailed);
        }

        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = _workDir,
        };
        foreach (var a in command.PrefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception)
        {
            return Plain(CliRunOutcome.NotStarted);
        }
        if (process is null) return Plain(CliRunOutcome.NotStarted);

        using (process)
        {
            using var overflowCts = new CancellationTokenSource();
            using var timeoutCts = new CancellationTokenSource(timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, overflowCts.Token);
            var overflow = 0;
            void OnOverflow()
            {
                Interlocked.Exchange(ref overflow, 1);
                try { overflowCts.Cancel(); } catch (ObjectDisposedException) { }
            }

            var stdinTask = WriteStdinAsync(process, stdin);
            var outTask = ReadBoundedAsync(process.StandardOutput, MaxStdoutChars, OnOverflow);
            var errTask = ReadBoundedAsync(process.StandardError, MaxStderrChars, OnOverflow);
            var all = Task.WhenAll(stdinTask, outTask, errTask);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await all.WaitAsync(linked.Token).ConfigureAwait(false);
                if (Volatile.Read(ref overflow) == 1) return Plain(CliRunOutcome.OutputTooLarge);
                return new CliRunResult(CliRunOutcome.Exited, process.ExitCode, await outTask.ConfigureAwait(false),
                    await errTask.ConfigureAwait(false), _time.GetElapsedTime(start));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                try
                {
                    await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), all)
                        .WaitAsync(KillWait).ConfigureAwait(false);
                }
                catch (Exception) { }

                var outcome = Volatile.Read(ref overflow) == 1 ? CliRunOutcome.OutputTooLarge
                    : ct.IsCancellationRequested ? CliRunOutcome.Cancelled
                    : CliRunOutcome.TimedOut;
                return Plain(outcome);
            }
        }
    }

    private static async Task WriteStdinAsync(Process process, string? stdin)
    {
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars, Action onOverflow)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                if (n == 0) break;
                if (sb.Length + n > maxChars)
                {
                    onOverflow();
                    break;
                }
                sb.Append(buf, 0, n);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return sb.ToString();
    }

    public async Task<CliRunResult> RunPromptAsync(ClaudeCliCommand command, ModelRequest request, bool safeMode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);

        if (Encoding.UTF8.GetByteCount(request.UserPrompt) > MaxPromptBytes)
            return new CliRunResult(CliRunOutcome.PromptTooLarge, null, "", "", TimeSpan.Zero);

        if (Interlocked.Exchange(ref _cleaned, 1) == 0) CleanupStaleFiles();

        string path;
        try
        {
            Directory.CreateDirectory(_workDir);
            path = Path.Combine(_workDir, $"sp-{Guid.NewGuid():N}.md");
            File.WriteAllText(path, request.SystemPrompt, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CliRunResult(CliRunOutcome.SetupFailed, null, "", "", TimeSpan.Zero);
        }

        try
        {
            var args = ClaudeCliArgs.Build(request.Model, request.Effort, request.JsonSchema, path, safeMode);
            return await RunAsync(command, args, request.UserPrompt, request.Timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes sp-*.md in workDir last written more than StaleAge ago. Swallows I/O errors.</summary>
    public void CleanupStaleFiles()
    {
        try
        {
            if (!Directory.Exists(_workDir)) return;
            var cutoff = _time.GetUtcNow().UtcDateTime - StaleAge;
            foreach (var f in Directory.EnumerateFiles(_workDir, "sp-*.md"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Reads the CLI's result envelope tolerantly and classifies errors (SPEC 19.3).</summary>
public static class ClaudeCliOutput
{
    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex RateLimitRx = new(
        @"usage limit|rate[ _]limit|limit reached|hit your (\w+ )?limit|overloaded|\b429\b|\b529\b", Opts, RxTimeout);
    private static readonly Regex AuthRx = new(
        @"not logged in|/login|invalid api key|authentication|\b401\b|oauth", Opts, RxTimeout);
    private static readonly Regex UnixResetRx = new(@"\|\s*(\d{9,11})\b", Opts, RxTimeout);
    private static readonly Regex ResetsRx = new(
        @"resets\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b(?:\s*\(([A-Za-z0-9_+\-/]+)\))?", Opts, RxTimeout);

    /// <summary>Pure, never throws. duration goes into Ok.Duration. now/localZone are for RetryAt.</summary>
    public static ModelResult Parse(int exitCode, string stdout, string stderr, TimeSpan duration,
        DateTimeOffset now, TimeZoneInfo localZone)
    {
        try
        {
            return ParseCore(exitCode, stdout ?? "", stderr ?? "", duration, now, localZone);
        }
        catch (Exception)
        {
            return new ModelResult.Failed(ModelFailure.BadOutput, null);
        }
    }

    private static ModelResult ParseCore(int exitCode, string stdout, string stderr, TimeSpan duration,
        DateTimeOffset now, TimeZoneInfo localZone)
    {
        var owner = FindEnvelope(stdout, out var env);
        try
        {
            var notJson = owner is null;
            bool? isError = null;
            string? subtype = null, result = null;
            var errors = new List<string>();
            int? apiStatus = null, inTok = null, outTok = null;
            JsonElement structured = default;

            if (!notJson)
            {
                if (env.TryGetProperty("is_error", out var ie) && ie.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    isError = ie.ValueKind == JsonValueKind.True;
                if (env.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String) subtype = st.GetString();
                if (env.TryGetProperty("result", out var rs) && rs.ValueKind == JsonValueKind.String) result = rs.GetString();
                if (env.TryGetProperty("errors", out var er) && er.ValueKind == JsonValueKind.Array)
                    foreach (var e in er.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) errors.Add(s);
                if (env.TryGetProperty("api_error_status", out var api) && api.ValueKind == JsonValueKind.Number
                    && api.TryGetInt32(out var apiInt)) apiStatus = apiInt;
                if (env.TryGetProperty("structured_output", out var so)) structured = so;
                if (env.TryGetProperty("usage", out var us) && us.ValueKind == JsonValueKind.Object)
                {
                    if (us.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
                        && it.TryGetInt32(out var i)) inTok = i;
                    if (us.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number
                        && ot.TryGetInt32(out var o)) outTok = o;
                }
            }

            var isErr = notJson || exitCode != 0 || isError == true
                || (subtype is not null && subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase));

            if (!isErr)
            {
                if (structured.ValueKind == JsonValueKind.Object)
                    return new ModelResult.Ok(structured.Clone(), duration, inTok, outTok);
                if (result is not null && TryParseObject(StripFence(result.Trim()), out var fromResult))
                    return new ModelResult.Ok(fromResult, duration, inTok, outTok);
                return new ModelResult.Failed(ModelFailure.NoStructuredOutput, null);
            }

            var parts = new List<string>();
            if (result is not null) parts.Add(result);
            parts.AddRange(errors);
            if (subtype is not null) parts.Add(subtype);
            parts.Add(stderr);
            if (notJson) parts.Add(stdout);
            var text = string.Join("\n", parts);

            if (apiStatus is 429 or 529) return new ModelResult.RateLimited(RetryAt(text, now, localZone));
            if (apiStatus is 401 or 403) return new ModelResult.NotSignedIn();
            if (IsMatch(RateLimitRx, text)) return new ModelResult.RateLimited(RetryAt(text, now, localZone));
            if (IsMatch(AuthRx, text)) return new ModelResult.NotSignedIn();
            if (subtype == "error_max_structured_output_retries")
                return new ModelResult.Failed(ModelFailure.NoStructuredOutput, null);
            if (notJson && exitCode == 0) return new ModelResult.Failed(ModelFailure.BadOutput, null);
            return new ModelResult.Failed(ModelFailure.ProcessExit, exitCode);
        }
        finally
        {
            owner?.Dispose();
        }
    }

    private static bool IsMatch(Regex rx, string text)
    {
        try { return rx.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static JsonDocument? TryParseDoc(string s)
    {
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    private static bool IsResultObject(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t)
        && t.ValueKind == JsonValueKind.String && t.GetString() == "result";

    /// <summary>Returns the document that owns the envelope, or null (no envelope).</summary>
    private static JsonDocument? FindEnvelope(string stdout, out JsonElement envelope)
    {
        envelope = default;
        var whole = TryParseDoc(stdout.Trim());
        if (whole is not null)
        {
            if (whole.RootElement.ValueKind == JsonValueKind.Object)
            {
                envelope = whole.RootElement;
                return whole;
            }
            if (whole.RootElement.ValueKind == JsonValueKind.Array)
            {
                JsonElement? last = null;
                foreach (var e in whole.RootElement.EnumerateArray())
                    if (IsResultObject(e)) last = e;
                if (last is { } found)
                {
                    envelope = found;
                    return whole;
                }
            }
            whole.Dispose();
        }

        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var d = TryParseDoc(line);
            if (d is null) continue;
            if (IsResultObject(d.RootElement))
            {
                envelope = d.RootElement;
                return d;
            }
            d.Dispose();
        }
        return null;
    }

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var lines = s.Split('\n');
        if (lines.Length < 3 || lines[^1].Trim() != "```") return s;
        return string.Join("\n", lines[1..^1]);
    }

    private static bool TryParseObject(string s, out JsonElement clone)
    {
        clone = default;
        using var d = TryParseDoc(s);
        if (d is null || d.RootElement.ValueKind != JsonValueKind.Object) return false;
        clone = d.RootElement.Clone();
        return true;
    }

    private static DateTimeOffset? RetryAt(string text, DateTimeOffset now, TimeZoneInfo localZone)
    {
        try
        {
            var m = UnixResetRx.Match(text);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var secs))
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(secs);
                if (at >= now - TimeSpan.FromDays(8) && at <= now + TimeSpan.FromDays(8)) return at;
            }

            var r = ResetsRx.Match(text);
            if (!r.Success) return null;
            var hour = int.Parse(r.Groups[1].Value);
            var minute = r.Groups[2].Success ? int.Parse(r.Groups[2].Value) : 0;
            if (hour < 1 || hour > 12 || minute > 59) return null;
            var pm = r.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            var h24 = hour % 12 + (pm ? 12 : 0);

            var zone = localZone;
            if (r.Groups[4].Success)
            {
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(r.Groups[4].Value); }
                catch (Exception) { zone = localZone; }
            }

            var nowInZone = TimeZoneInfo.ConvertTime(now, zone);
            var local = new DateTime(nowInZone.Year, nowInZone.Month, nowInZone.Day, h24, minute, 0, DateTimeKind.Unspecified);
            var result = ToOffset(local, zone);
            if (result <= now) result = ToOffset(local.AddDays(1), zone);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

public static class ClaudeCliStatusTexts
{
    public const string NotFound =
        "✗ Claude Code wasn't found. Install it from https://claude.com/claude-code, sign in once in a terminal, then press Check.";
    public const string NotSignedIn =
        "✗ Claude Code is installed but not signed in. Open a terminal, run claude and sign in, then press Check.";
    public const string UpdateHint = " · update Claude Code for best results";

    public static string TooOld(string version) =>
        $"✗ Claude Code {version} is too old for AI drafts. Run \"claude update\", then press Check.";

    public static string NoAnswer(string step) =>
        $"✗ Claude Code didn't answer ({step}). Press Check to try again.";

    public static string Ready(string version, string? authMethod, string? subscriptionType, bool safeModeListed)
    {
        var sb = new StringBuilder($"✓ Claude Code {version} · signed in");
        if (!string.IsNullOrEmpty(authMethod))
        {
            sb.Append(" (").Append(authMethod);
            if (!string.IsNullOrEmpty(subscriptionType)) sb.Append(", ").Append(subscriptionType);
            sb.Append(')');
        }
        if (!safeModeListed) sb.Append(UpdateHint);
        return sb.ToString();
    }
}

/// <summary>The default IDraftModel: the user's Claude Code CLI run headless (SPEC 19.3).</summary>
public sealed class ClaudeCliModel : IDraftModel
{
    public static readonly TimeSpan StatusCacheTime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HelpTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);

    private static readonly Regex VersionRx = new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private enum ProbeKind { NotFound, TooOld, NotSignedIn, NoAnswer, Ready }

    private sealed record Probe(ProbeKind Kind, string Version, bool SafeMode, string? AuthMethod,
        string? SubscriptionType, string Text, string Key, DateTimeOffset CachedAt, ClaudeCliCommand? Command);

    private readonly Func<AiDraftingSettings> _settings;
    private readonly ClaudeCliLocator _locator;
    private readonly IClaudeCliRunner _runner;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Probe? _cached;

    public ClaudeCliModel(Func<AiDraftingSettings> settings, ClaudeCliLocator locator, IClaudeCliRunner runner,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _time = time ?? TimeProvider.System;
        _log = logger;
    }

    /// <summary>Drops the cached status. Settings Check calls this before CheckAsync (M11.8).</summary>
    public void InvalidateStatus() => Volatile.Write(ref _cached, null);

    public async Task<ModelStatus> CheckAsync(CancellationToken ct)
    {
        var p = await GetProbeAsync(ct).ConfigureAwait(false);
        return new ModelStatus(p.Kind == ProbeKind.Ready, p.Text);
    }

    private static string KeyOf(ClaudeCliCommand c) => c.FileName + "\0" + string.Join("\0", c.PrefixArgs);

    private async Task<Probe> GetProbeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var command = _locator.Find(_settings().ClaudeCliPath);
            if (command is null) return Finish(new Probe(ProbeKind.NotFound, "", false, null, null,
                ClaudeCliStatusTexts.NotFound, "", _time.GetUtcNow(), null));

            var key = KeyOf(command);
            var cached = Volatile.Read(ref _cached);
            if (cached is not null && cached.Key == key && _time.GetUtcNow() - cached.CachedAt < StatusCacheTime)
                return cached;

            var probe = await ProbeAsync(command, key, ct).ConfigureAwait(false);
            if (probe.Kind == ProbeKind.Ready) Volatile.Write(ref _cached, probe);
            return Finish(probe);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Probe Finish(Probe p)
    {
        _log?.LogInformation("Claude Code status: {Kind}, version {Version}, safe-mode {SafeMode}",
            p.Kind, p.Version, p.SafeMode);
        return p;
    }

    private Probe Fail(ProbeKind kind, string text, string key, ClaudeCliCommand command, string version = "") =>
        new(kind, version, false, null, null, text, key, _time.GetUtcNow(), command);

    private async Task<Probe> ProbeAsync(ClaudeCliCommand command, string key, CancellationToken ct)
    {
        Probe NoAnswer(string step) => Fail(ProbeKind.NoAnswer, ClaudeCliStatusTexts.NoAnswer(step), key, command);
        Probe NotFound() => Fail(ProbeKind.NotFound, ClaudeCliStatusTexts.NotFound, key, command);

        // 1. --version
        var v = await _runner.RunAsync(command, ClaudeCliArgs.Version, null, VersionTimeout, ct).ConfigureAwait(false);
        switch (v.Outcome)
        {
            case CliRunOutcome.NotStarted: return NotFound();
            case CliRunOutcome.Cancelled: throw new OperationCanceledException(ct);
            case CliRunOutcome.Exited when v.ExitCode == 0: break;
            default: return NoAnswer("version");
        }
        var vm = VersionRx.Match(v.Stdout);
        if (!vm.Success) return NoAnswer("version");
        var version = vm.Value;

        // 2. --help
        var h = await _runner.RunAsync(command, ClaudeCliArgs.Help, null, HelpTimeout, ct).ConfigureAwait(false);
        switch (h.Outcome)
        {
            case CliRunOutcome.NotStarted: return NotFound();
            case CliRunOutcome.Cancelled: throw new OperationCanceledException(ct);
            case CliRunOutcome.Exited when h.ExitCode == 0: break;
            default: return NoAnswer("help");
        }
        var helpText = h.Stdout + "\n" + h.Stderr;
        foreach (var flag in ClaudeCliArgs.RequiredHelpFlags)
            if (!ClaudeCliArgs.HelpMentions(helpText, flag))
                return Fail(ProbeKind.TooOld, ClaudeCliStatusTexts.TooOld(version), key, command, version);
        var safeMode = ClaudeCliArgs.HelpMentions(helpText, ClaudeCliArgs.SafeModeFlag);

        // 3. auth status --json (read whatever the exit code)
        var a = await _runner.RunAsync(command, ClaudeCliArgs.AuthStatus, null, AuthTimeout, ct).ConfigureAwait(false);
        switch (a.Outcome)
        {
            case CliRunOutcome.NotStarted: return NotFound();
            case CliRunOutcome.Cancelled: throw new OperationCanceledException(ct);
            case CliRunOutcome.Exited: break;
            default: return NoAnswer("auth status");
        }

        bool loggedIn;
        string? authMethod, subscriptionType;
        try
        {
            using var doc = JsonDocument.Parse(a.Stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return NoAnswer("auth status");
            loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
            authMethod = Clean(root.TryGetProperty("authMethod", out var am) ? am : default);
            subscriptionType = Clean(root.TryGetProperty("subscriptionType", out var sub) ? sub : default);
        }
        catch (JsonException)
        {
            return NoAnswer("auth status");
        }

        if (!loggedIn)
            return new Probe(ProbeKind.NotSignedIn, version, safeMode, null, null,
                ClaudeCliStatusTexts.NotSignedIn, key, _time.GetUtcNow(), command);

        return new Probe(ProbeKind.Ready, version, safeMode, authMethod, subscriptionType,
            ClaudeCliStatusTexts.Ready(version, authMethod, subscriptionType, safeMode), key, _time.GetUtcNow(), command);
    }

    private static string? Clean(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return null;
        var s = e.GetString();
        if (s is null) return null;
        var sb = new StringBuilder();
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        var t = sb.ToString().Trim();
        if (t.Length > 40) t = t[..40];
        return t.Length == 0 ? null : t;
    }

    public async Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = _time.GetTimestamp();
        var (result, exit) = await RunCoreAsync(request, ct).ConfigureAwait(false);

        if (result is ModelResult.NotSignedIn) InvalidateStatus();

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
        _log?.LogInformation("Claude call: {ResultKind} in {Ms} ms, tokens {In}/{Out}, exit {Exit}",
            kind, (long)_time.GetElapsedTime(start).TotalMilliseconds, inTok, outTok, exit);
        return result;
    }

    private async Task<(ModelResult, int?)> RunCoreAsync(ModelRequest request, CancellationToken ct)
    {
        static (ModelResult, int?) F(ModelFailure k) => (new ModelResult.Failed(k, null), null);

        if (request.Timeout <= TimeSpan.Zero || request.SystemPrompt is null || request.UserPrompt is null
            || !ClaudeCliArgs.IsValidModel(request.Model) || !ClaudeCliArgs.IsValidEffort(request.Effort)
            || !ClaudeCliArgs.TryCompactSchema(request.JsonSchema, out _))
            return F(ModelFailure.BadInput);

        Probe probe;
        try
        {
            probe = await GetProbeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return F(ModelFailure.Cancelled);
        }

        switch (probe.Kind)
        {
            case ProbeKind.NotFound:
            case ProbeKind.TooOld:
                return F(ModelFailure.NotInstalled);
            case ProbeKind.NotSignedIn:
                return (new ModelResult.NotSignedIn(), null);
            case ProbeKind.NoAnswer:
                return F(ModelFailure.ProcessExit);
        }

        CliRunResult run;
        try
        {
            run = await _runner.RunPromptAsync(probe.Command!, request, probe.SafeMode, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return F(ModelFailure.Cancelled);
        }

        switch (run.Outcome)
        {
            case CliRunOutcome.Exited:
                return (ClaudeCliOutput.Parse(run.ExitCode ?? -1, run.Stdout, run.Stderr, run.Duration,
                    _time.GetUtcNow(), _time.LocalTimeZone), run.ExitCode);
            case CliRunOutcome.NotStarted:
                InvalidateStatus();
                return F(ModelFailure.NotInstalled);
            case CliRunOutcome.PromptTooLarge: return F(ModelFailure.BadInput);
            case CliRunOutcome.TimedOut: return F(ModelFailure.Timeout);
            case CliRunOutcome.Cancelled: return F(ModelFailure.Cancelled);
            case CliRunOutcome.OutputTooLarge: return F(ModelFailure.BadOutput);
            default: return F(ModelFailure.ProcessExit);
        }
    }
}

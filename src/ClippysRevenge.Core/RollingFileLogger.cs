using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public sealed class RollingFileLogOptions
{
    /// <summary>Folder for <c>clippy-YYYYMMDD.log</c> (SPEC 5.1).</summary>
    public required string Directory { get; init; }

    /// <summary>Read on every call, so a LogLevel setting change is live.</summary>
    public Func<LogLevel> MinLevel { get; init; } = static () => LogLevel.Information;

    /// <summary>Secrets (the token) that are replaced by <see cref="LogRedactor.Mask"/> wherever they appear.</summary>
    public Func<IEnumerable<string?>> Secrets { get; init; } = static () => [];

    public int RetainDays { get; init; } = 7;
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Categories starting with these prefixes are capped at Warning (framework chatter can carry request details).</summary>
    public IReadOnlyList<string> QuietPrefixes { get; init; } = ["Microsoft.", "System.", "ModelContextProtocol"];
}

public static partial class LogRedactor
{
    public const string Mask = "[redacted]";
    private const int MinSecretLength = 8;

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s""']+")]
    private static partial Regex BearerRegex();

    public static string Redact(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
        {
            if (!string.IsNullOrEmpty(s) && s.Length >= MinSecretLength)
            {
                text = text.Replace(s, Mask, StringComparison.Ordinal);
            }
        }
        return BearerRegex().Replace(text, "Bearer " + Mask);
    }
}

/// <summary>
/// Small rolling file logger (SPEC 2, 5.1): one file per local day, old files deleted.
/// Callers must not pass tokens, bodies or search text; the token is also masked defensively.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const string FilePrefix = "clippy-";
    public const string FileSuffix = ".log";

    private readonly RollingFileLogOptions _options;
    private readonly Lock _lock = new();
    private string? _lastFile;
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainDays, 1);
        _options = options;
    }

    public static string FileNameFor(DateTime localDay) =>
        FilePrefix + localDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileSuffix;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }

    private bool IsEnabled(string category, LogLevel level)
    {
        if (level == LogLevel.None)
        {
            return false;
        }
        var min = _options.MinLevel();
        if (min == LogLevel.None)
        {
            return false;
        }
        if (level < LogLevel.Warning && _options.QuietPrefixes.Any(p => category.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }
        return level >= min;
    }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var now = _options.Time.GetLocalNow();
        var sb = new StringBuilder();
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(Abbrev(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (ex is not null)
        {
            sb.Append(Environment.NewLine).Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (ex.StackTrace is { } st)
            {
                sb.Append(Environment.NewLine).Append(st);
            }
        }
        var line = LogRedactor.Redact(sb.ToString(), _options.Secrets()) + Environment.NewLine;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                System.IO.Directory.CreateDirectory(_options.Directory);
                var day = now.Date;
                var name = FileNameFor(day);
                if (_lastFile != name)
                {
                    _lastFile = name;
                    Purge(day);
                }
                File.AppendAllText(Path.Combine(_options.Directory, name), line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }

    /// <summary>Deletes clippy-YYYYMMDD.log files older than the retention window. Other files are left alone.</summary>
    private void Purge(DateTime today)
    {
        var oldestKept = today.AddDays(-(_options.RetainDays - 1));
        foreach (var path in System.IO.Directory.EnumerateFiles(_options.Directory, FilePrefix + "*" + FileSuffix))
        {
            var file = Path.GetFileName(path);
            var stamp = file[FilePrefix.Length..^FileSuffix.Length];
            if (stamp.Length == 8 && DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d < oldestKept)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Retry on the next day change.
                }
            }
        }
    }

    private static string Abbrev(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            owner.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

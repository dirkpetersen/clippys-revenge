using Microsoft.Extensions.Logging;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private const string Token = "s3cr3t-token-ABCDEFGH1234567890";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-log-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private LogLevel _level = LogLevel.Information;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private RollingFileLoggerProvider Make(int retain = 7) => new(new RollingFileLogOptions
    {
        Directory = _dir,
        MinLevel = () => _level,
        Secrets = () => [Token],
        RetainDays = retain,
        Time = _time,
    });

    private string Today => RollingFileLoggerProvider.FileNameFor(_time.GetLocalNow().Date);

    [Fact]
    public void WritesToDatedFile()
    {
        using var p = Make();
        p.CreateLogger("Cat").LogInformation("hello {X}", 5);
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.Matches(@"^clippy-\d{8}\.log$", Today);
        Assert.Contains("INF Cat: hello 5", text);
    }

    [Fact]
    public void RotatesAtDayChange()
    {
        using var p = Make();
        var log = p.CreateLogger("Cat");
        log.LogInformation("day one");
        var first = Today;
        _time.Advance(TimeSpan.FromDays(1));
        log.LogInformation("day two");
        Assert.NotEqual(first, Today);
        Assert.Contains("day one", File.ReadAllText(Path.Combine(_dir, first)));
        Assert.DoesNotContain("day one", File.ReadAllText(Path.Combine(_dir, Today)));
        Assert.Contains("day two", File.ReadAllText(Path.Combine(_dir, Today)));
    }

    [Fact]
    public void KeepsSevenDaysAndLeavesOtherFiles()
    {
        Directory.CreateDirectory(_dir);
        var today = _time.GetLocalNow().Date;
        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(_dir, RollingFileLoggerProvider.FileNameFor(today.AddDays(-i))), "x");
        }
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "clippy-abc.log"), "x");

        using var p = Make();
        p.CreateLogger("Cat").LogInformation("go");

        var logs = Directory.GetFiles(_dir, "clippy-2*.log").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(7, logs.Count);
        Assert.Contains(RollingFileLoggerProvider.FileNameFor(today.AddDays(-6)), logs);
        Assert.DoesNotContain(RollingFileLoggerProvider.FileNameFor(today.AddDays(-7)), logs);
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "clippy-abc.log")));
    }

    [Fact]
    public void LevelIsLive()
    {
        using var p = Make();
        var log = p.CreateLogger("Cat");
        log.LogDebug("dbg-one");
        _level = LogLevel.Debug;
        log.LogDebug("dbg-two");
        _level = LogLevel.None;
        log.LogCritical("crit-none");
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.DoesNotContain("dbg-one", text);
        Assert.Contains("dbg-two", text);
        Assert.DoesNotContain("crit-none", text);
    }

    [Fact]
    public void TokenAndBearerAreRedactedInMessagesAndExceptions()
    {
        using var p = Make();
        var log = p.CreateLogger("Cat");
        log.LogError(new InvalidOperationException("bad " + Token), "failed with {T} and Authorization: Bearer abc.def-123", Token);
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("abc.def-123", text);
        Assert.Contains(LogRedactor.Mask, text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void FrameworkChatterBelowWarningIsDropped()
    {
        _level = LogLevel.Trace;
        using var p = Make();
        p.CreateLogger("ModelContextProtocol.Server.X").LogInformation("params-secret-search");
        p.CreateLogger("Microsoft.AspNetCore.Hosting").LogDebug("req");
        p.CreateLogger("ModelContextProtocol.Server.X").LogWarning("warned");
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.DoesNotContain("params-secret-search", text);
        Assert.DoesNotContain("req", text.Replace("[redacted]", ""));
        Assert.Contains("warned", text);
    }

    [Fact]
    public void UnwritableFolderDoesNotThrow()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "clippy-blk-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            using var p = new RollingFileLoggerProvider(new RollingFileLogOptions { Directory = Path.Combine(blocker, "sub") });
            p.CreateLogger("Cat").LogError("nope");
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Redact_AnthropicKeys()
    {
        Assert.Equal("key [redacted] end", LogRedactor.Redact("key sk-ant-api03-AbC_d-9 end", []));
        Assert.Equal("[redacted] and [redacted]", LogRedactor.Redact("sk-ant-a1 and sk-ant-b2", []));
        Assert.Equal("sk-ant-", LogRedactor.Redact("sk-ant-", []));
        Assert.Equal("x[redacted]", LogRedactor.Redact("xsk-ant-abc", []));
        Assert.Equal("x [redacted] y", LogRedactor.Redact("x MYSECRETKEY123 y", ["tok-not-here", null, "MYSECRETKEY123"]));
    }

    [Fact]
    public void ApiKeySecret_IsMaskedInMessageAndException()
    {
        const string apiKey = "plainapikey-98765";
        using (var p = new RollingFileLoggerProvider(new RollingFileLogOptions
        {
            Directory = _dir,
            Secrets = () => [Token, apiKey],
            Time = _time,
        }))
        {
            p.CreateLogger("Cat").LogError(new InvalidOperationException("bad sk-ant-api03-zzz"), "k={0}", apiKey);
        }
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.DoesNotContain(apiKey, text);
        Assert.DoesNotContain("sk-ant-api03-zzz", text);
        Assert.True(text.Split("[redacted]").Length - 1 >= 2);
    }

    [Fact]
    public void RejectsZeroRetention() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(0));

    [Fact]
    public void McpExceptionMessage_IsNotWritten()
    {
        _level = LogLevel.Debug;
        using var p = Make();
        p.CreateLogger("ModelContextProtocol.Server.McpServer").LogError(
            new ModelContextProtocol.McpException("SQL error: near \"zebra7731\": syntax error."),
            "\"query_index\" threw an unhandled exception.");
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.Contains("\"query_index\" threw an unhandled exception.", text);
        Assert.Contains("ModelContextProtocol.McpException", text);
        Assert.DoesNotContain("zebra7731", text);
    }

    [Fact]
    public void McpProtocolExceptionMessage_IsNotWritten()
    {
        _level = LogLevel.Debug;
        using var p = Make();
        p.CreateLogger("Cat").LogError(
            new ModelContextProtocol.McpProtocolException("bad zebra7731", ModelContextProtocol.McpErrorCode.InvalidParams), "x");
        var text = File.ReadAllText(Path.Combine(_dir, Today));
        Assert.Contains("ModelContextProtocol.McpProtocolException", text);
        Assert.DoesNotContain("zebra7731", text);
    }

    [Fact]
    public void OtherExceptionMessages_AreStillWritten()
    {
        using var p = Make();
        p.CreateLogger("Cat").LogError(new InvalidOperationException("disk zebra7731"), "x");
        Assert.Contains("System.InvalidOperationException: disk zebra7731", File.ReadAllText(Path.Combine(_dir, Today)));
    }
}

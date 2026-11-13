using System.Diagnostics;
using System.Text;

namespace ClippysRevenge.Core.Tests;

public sealed class ClaudeCliRunnerTests : IDisposable
{
    private readonly string _scripts = Path.Combine(Path.GetTempPath(), "clippy-fc-s-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "clippy-fc-w-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in new[] { _scripts, _work })
        {
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private ClaudeCliRunner Runner() => new(_work);

    private static ModelRequest Req(string user = "<email_to_answer>Grüße ✓</email_to_answer>", TimeSpan? timeout = null) =>
        new("SYS ü ✓\nline2", user, DraftModel.ReplySchema, "claude-opus-5-5", "low", timeout ?? TimeSpan.FromSeconds(60), 8000);

    private string Read(string name) => File.ReadAllText(Path.Combine(_scripts, name), new UTF8Encoding(false));

    private static async Task<bool> WaitForAsync(Func<bool> cond, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < max)
        {
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private async Task AssertPidGoneAsync()
    {
        var pidFile = Path.Combine(_scripts, "pid.txt");
        if (!File.Exists(pidFile)) return; // killed before the script wrote it
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(await WaitForAsync(() => Gone(pid), TimeSpan.FromSeconds(20)), "process still alive");
    }

    // ---- scenarios ----

    private async Task EchoScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Echo);
        var req = Req();
        var r = await Runner().RunPromptAsync(cmd, req, true, CancellationToken.None);
        Assert.Equal(CliRunOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.IsType<ModelResult.Ok>(ClaudeCliOutput.Parse(r.ExitCode!.Value, r.Stdout, r.Stderr, r.Duration, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
        Assert.Equal(req.UserPrompt, Read("stdin.txt"));
        Assert.Equal("yes", Read("sp-exists.txt").Trim());
        Assert.Equal(Encoding.UTF8.GetBytes(req.SystemPrompt), File.ReadAllBytes(Path.Combine(_scripts, "sp-copy.md")));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        char[] seps = ['\\', '/'];
        Assert.True(string.Equals(Path.GetFullPath(_work).TrimEnd(seps), Path.GetFullPath(Read("pwd.txt").Trim()).TrimEnd(seps), cmp),
            "pwd was " + Read("pwd.txt"));
        Assert.Contains("oops", r.Stderr);
        Assert.Empty(Directory.GetFiles(_work, "sp-*.md"));
    }

    [Fact]
    public Task Echo_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return EchoScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task Echo_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return EchoScenario();
    }

    [Fact]
    public async Task Args_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Echo);
        var r = await Runner().RunPromptAsync(cmd, Req(), true, CancellationToken.None);
        Assert.Equal(CliRunOutcome.Exited, r.Outcome);
        var lines = File.ReadAllLines(Path.Combine(_scripts, "args.txt"));
        var i = Array.IndexOf(lines, "--system-prompt-file");
        var sp = lines[i + 1];
        Assert.Matches(@"^sp-[0-9a-f]{32}\.md$", Path.GetFileName(sp));
        Assert.Equal(Path.GetFullPath(_work), Path.GetDirectoryName(sp));
        Assert.Equal(ClaudeCliArgs.Build("claude-opus-5-5", "low", DraftModel.ReplySchema, sp, true), lines);
        Assert.Contains(string.Empty, lines);
    }

    private async Task TimeoutScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Sleep);
        var sw = Stopwatch.StartNew();
        var r = await Runner().RunPromptAsync(cmd, Req(timeout: TimeSpan.FromSeconds(6)), false, CancellationToken.None);
        Assert.Equal(CliRunOutcome.TimedOut, r.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45), "took " + sw.Elapsed);
        await AssertPidGoneAsync();
        Assert.Empty(Directory.GetFiles(_work, "sp-*.md"));
    }

    [Fact]
    public Task Timeout_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return TimeoutScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task Timeout_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return TimeoutScenario();
    }

    private async Task CancelScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Sleep);
        using var cts = new CancellationTokenSource();
        var task = Runner().RunAsync(cmd, [], null, TimeSpan.FromSeconds(120), cts.Token);
        // Wait until the script is really running, then cancel.
        await WaitForAsync(() => File.Exists(Path.Combine(_scripts, "pid.txt")), TimeSpan.FromSeconds(30));
        await Task.Delay(500);
        cts.Cancel();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(CliRunOutcome.Cancelled, r.Outcome);
        await AssertPidGoneAsync();
    }

    [Fact]
    public Task Cancel_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return CancelScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task Cancel_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return CancelScenario();
    }

    private async Task Exit3Scenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Exit3);
        var r = await Runner().RunAsync(cmd, [], null, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(CliRunOutcome.Exited, r.Outcome);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("partial", r.Stdout);
        Assert.Contains("bad thing", r.Stderr);
    }

    [Fact]
    public Task Exit3_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return Exit3Scenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task Exit3_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return Exit3Scenario();
    }

    private async Task BigScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.BigStdout);
        var r = await Runner().RunAsync(cmd, [], null, TimeSpan.FromSeconds(90), CancellationToken.None);
        Assert.Equal(CliRunOutcome.OutputTooLarge, r.Outcome);
        Assert.Equal("", r.Stdout);
    }

    [Fact]
    public Task BigStdout_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return BigScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task BigStdout_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return BigScenario();
    }

    private async Task PromptTooLargeScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Echo);
        var r = await Runner().RunPromptAsync(cmd, Req(new string('a', 2_097_153)), false, CancellationToken.None);
        Assert.Equal(CliRunOutcome.PromptTooLarge, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_scripts, "stdin.txt")));
        Assert.False(Directory.Exists(_work) && Directory.GetFiles(_work, "sp-*.md").Length > 0);
    }

    [Fact]
    public Task PromptTooLarge_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return PromptTooLargeScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task PromptTooLarge_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return PromptTooLargeScenario();
    }

    [Fact]
    public async Task MissingExecutable_NotStarted()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), OperatingSystem.IsWindows() ? "x.exe" : "x");
        var r = await Runner().RunAsync(new ClaudeCliCommand(missing, []), [], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(CliRunOutcome.NotStarted, r.Outcome);
    }

    private async Task NoWorkDirScenario()
    {
        Assert.False(Directory.Exists(_work));
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Echo);
        var r = await Runner().RunPromptAsync(cmd, Req(), false, CancellationToken.None);
        Assert.Equal(CliRunOutcome.Exited, r.Outcome);
        Assert.True(Directory.Exists(_work));
    }

    [Fact]
    public Task WorkDirCreated_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return NoWorkDirScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task WorkDirCreated_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return NoWorkDirScenario();
    }

    private async Task NullStdinScenario()
    {
        var cmd = FakeClaude.Create(_scripts, FakeBehavior.Echo);
        var r = await Runner().RunAsync(cmd, [], null, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(CliRunOutcome.Exited, r.Outcome);
        Assert.Equal("", Read("stdin.txt"));
    }

    [Fact]
    public Task NullStdin_Sh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh variant");
        return NullStdinScenario();
    }

    [Fact]
    [Trait("os", "windows")]
    public Task NullStdin_PowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        return NullStdinScenario();
    }

    [Fact]
    public void CleanupStaleFiles_DeletesOnlyOldSpFiles()
    {
        Directory.CreateDirectory(_work);
        var old = Path.Combine(_work, "sp-old.md");
        var fresh = Path.Combine(_work, "sp-new.md");
        var keep = Path.Combine(_work, "keep.txt");
        foreach (var f in new[] { old, fresh, keep }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(keep, DateTime.UtcNow.AddHours(-2));
        Runner().CleanupStaleFiles();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void CleanupStaleFiles_MissingDir_NoThrow() => Runner().CleanupStaleFiles();

    [Fact]
    public void Ctor_RejectsRelativeDir() => Assert.Throws<ArgumentException>(() => new ClaudeCliRunner("rel"));
}

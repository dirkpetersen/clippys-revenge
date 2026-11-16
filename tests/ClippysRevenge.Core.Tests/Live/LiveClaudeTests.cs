using System.Globalization;
using System.Text.Json;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// The user's own Claude Code CLI, for real (SPEC 19.12). Needs CLIPPY_LIVE=1 and CLIPPY_LIVE_CLAUDE=1. Uses
/// claude-haiku-5-5 with effort low (one tiny call), not the app default. No Outlook, no mail, nothing is sent.
/// Output holds no prompts, model text, account details or profile paths.
/// </summary>
[Collection(LiveOutlookCollection.Name)]   // for fx.Result and fx.TempRoot only; no COM
[Trait("live", "claude")]
[Trait("os", "windows")]
public sealed class LiveClaudeTests(LiveOutlook fx)
{
    private string WorkDir() => Path.Combine(fx.TempRoot, "ai");

    [Fact(Timeout = 120_000)]
    public async Task Cli_is_found_and_ready()
    {
        var model = LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;

        var cmd = ClaudeCliLocator.Default.Find("");
        if (cmd is null) Assert.Skip(LiveClaude.NotReady(ClaudeCliStatusTexts.NotFound));
        Assert.True(File.Exists(cmd.FileName), "The Claude Code file the locator returned does not exist.");

        var cli = LiveClaude.CreateModel(() => LiveClaude.Settings(model, DateTimeOffset.UtcNow), WorkDir());
        var status = await cli.CheckAsync(ct);
        if (!status.Ready) Assert.Skip(LiveClaude.NotReady(status.Text));

        Assert.True(status.Text.StartsWith("✓ Claude Code ", StringComparison.Ordinal),
            "The ready status text does not have the expected form.");
        var version = LiveClaude.VersionOf(status.Text);
        Assert.True(version != "?", "The ready status text has no version number.");

        var listed = !status.Text.Contains(ClaudeCliStatusTexts.UpdateHint, StringComparison.Ordinal);
        fx.Result("claude-status",
            $"ready, version {version}, safe-mode listed={(listed ? "true" : "false")}, exe={Path.GetFileName(cmd.FileName)}");
    }

    [Fact(Timeout = 180_000)]
    public async Task Tiny_structured_call_returns_ok_true()
    {
        var model = LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        var workDir = WorkDir();
        var cli = LiveClaude.CreateModel(() => LiveClaude.Settings(model, DateTimeOffset.UtcNow), workDir);
        await LiveClaude.RequireReadyAsync(cli, ct);

        var request = new ModelRequest(
            "You are a connectivity test. Answer only with JSON that matches the schema, with ok set to true.",
            "ping", LiveClaude.OkSchema, model, LiveClaude.Effort, TimeSpan.FromSeconds(120), 256);
        var result = await cli.RunAsync(request, ct);

        if (result is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
        if (result is ModelResult.NotSignedIn) Assert.Skip(LiveClaude.NotReady(ClaudeCliStatusTexts.NotSignedIn));
        if (result is ModelResult.Failed f)
            Assert.Fail($"The Claude call failed ({f.Kind}, code {f.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
        if (result is not ModelResult.Ok ok)
        {
            Assert.Fail("The Claude call returned an unexpected result.");
            return;
        }

        Assert.True(ok.Output.ValueKind == JsonValueKind.Object, "The Claude output is not a JSON object.");
        Assert.True(ok.Output.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True,
            "The Claude output does not have ok = true.");
        Assert.True(ok.Duration < TimeSpan.FromSeconds(120), "The Claude call took 120 seconds or more.");

        var leftovers = Directory.GetFiles(workDir, "sp-*.md");
        Assert.True(leftovers.Length == 0, "The Claude runner left its system-prompt file behind.");

        var inTok = ok.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var outTok = ok.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?";
        fx.Result("claude-call", $"ok in {AiHtmlChecks.Seconds(ok.Duration)} s, tokens {inTok}/{outTok}, model {model}");
    }

    [Fact(Timeout = 180_000)]
    public async Task Cli_call_reports_usage_and_writes_an_ai_calls_row()
    {
        var model = LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        var settings = LiveClaude.Settings(model, DateTimeOffset.UtcNow);
        var cli = LiveClaude.CreateModel(() => settings, WorkDir());
        await LiveClaude.RequireReadyAsync(cli, ct);

        using var indexes = new MailboxIndexes(Path.Combine(fx.TempRoot, "usage-index"), []);
        var usage = new AiUsage(() => indexes.For(""), TimeZoneInfo.Local);
        usage.Load();
        var rec = new UsageRecordingModel(cli, () => settings, usage);
        var start = DateTimeOffset.UtcNow;

        var request = new ModelRequest(
            "You are a connectivity test. Answer only with JSON that matches the schema, with ok set to true.",
            "ping", LiveClaude.OkSchema, model, LiveClaude.Effort, TimeSpan.FromSeconds(120), 256, ModelCallKind.Style);
        var result = await rec.RunAsync(request, ct);

        if (result is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
        if (result is ModelResult.NotSignedIn) Assert.Skip(LiveClaude.NotReady(ClaudeCliStatusTexts.NotSignedIn));
        if (result is ModelResult.Failed f)
            Assert.Fail($"The Claude call failed ({f.Kind}, code {f.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
        if (result is not ModelResult.Ok ok)
        {
            Assert.Fail("The Claude call returned an unexpected result.");
            return;
        }

        Assert.True(ok.Usage is { Input: > 0, Output: > 0 }, "The CLI envelope has no numeric usage.input_tokens/output_tokens.");
        var t = indexes.For("").AiUsageSince(start);
        Assert.True(t.Calls == 1 && t.UnknownCalls == 0 && t.Input == ok.Usage!.Input && t.Output == ok.Usage.Output
            && t.CacheRead == (ok.Usage.CacheRead ?? 0) && t.CacheCreation == (ok.Usage.CacheCreation ?? 0),
            "No ai_calls row matches the call.");
        Assert.True(usage.Today().Calls == 1, "The usage tally of today did not count exactly the call.");

        static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "absent";
        fx.Result("claude-usage",
            $"input {N(ok.Usage.Input)}, output {N(ok.Usage.Output)}, cache_read {N(ok.Usage.CacheRead)}, "
            + $"cache_creation {N(ok.Usage.CacheCreation)}, ai_calls row ok");
    }
}

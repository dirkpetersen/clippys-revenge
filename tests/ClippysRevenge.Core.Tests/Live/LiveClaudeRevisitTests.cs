using System.Globalization;
using System.Text.Json;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// The M16 model contract against the user's own Claude Code CLI (SPEC 23.7, 19.12). Needs CLIPPY_LIVE=1 and
/// CLIPPY_LIVE_CLAUDE=1. Synthetic input only, no mailbox data, no Outlook. Output holds the action name and numbers,
/// never the reason, body text or prompts.
/// </summary>
[Collection(LiveOutlookCollection.Name)]   // for fx.Result and fx.TempRoot only; no COM
[Trait("live", "claude")]
[Trait("os", "windows")]
public sealed class LiveClaudeRevisitTests(LiveOutlook fx)
{
    [Fact(Timeout = 180_000)]
    public async Task Revisit_schema_call_returns_a_parseable_reply()
    {
        var model = LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var cli = LiveClaude.CreateModel(() => LiveClaude.Settings(model, now), Path.Combine(fx.TempRoot, "ai-revisit"));
        await LiveClaude.RequireReadyAsync(cli, ct);

        var thread = new List<ThreadMessage>
        {
            new("m1", "Inbox", now.AddHours(-3), "Alex", "alex@example.org", "sam.tester@example.org", null,
                "Ski wax", [], "Who can bring the ski wax on Saturday?"),
        };
        var email = new PromptEmail("Alex", "alex@example.org", "sam.tester@example.org", null, now.AddMinutes(-5),
            "Ski wax", [], "Update: Robin already bought the wax, so nobody needs to bring it. See you Saturday!");
        var input = new DraftPromptInput(now, TimeZoneInfo.Utc, "UTC", "", false, thread, email, null);
        var request = new ModelRequest(
            DraftPrompt.RevisitSystemPrompt("Sam Tester", "sam.tester@example.org", DraftPrompt.NoStyleGuideText, false),
            DraftPrompt.RevisitUserPrompt(new RevisitPromptInput(input, "Hi Alex, I can bring the ski wax on Saturday. Sam", now.AddHours(-1))),
            DraftModel.RevisitSchema, model, LiveClaude.Effort, TimeSpan.FromSeconds(120), DraftModel.DraftMaxOutputTokens);

        var result = await cli.RunAsync(request, ct);
        if (result is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
        if (result is ModelResult.NotSignedIn) Assert.Skip(LiveClaude.NotReady(ClaudeCliStatusTexts.NotSignedIn));
        if (result is ModelResult.Failed f)
            Assert.Fail($"The revisit call failed ({f.Kind}, code {f.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
        if (result is not ModelResult.Ok ok)
        {
            Assert.Fail("The revisit call returned an unexpected result.");
            return;
        }

        var reply = DraftRevisit.Parse(ok.Output);
        Assert.True(reply is not null, "The revisit output doesn't match RevisitSchema.");
        Assert.True(reply.Confidence is >= 0.0 and <= 1.0, "The revisit confidence is outside 0..1.");
        fx.Result("claude-revisit", $"ok in {AiHtmlChecks.Seconds(ok.Duration)} s, action {reply.Action}, model {model}");
    }

    [Fact(Timeout = 180_000)]
    public async Task Opus_alias_call_succeeds()
    {
        LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        const string opus = "opus";
        var cli = LiveClaude.CreateModel(() => LiveClaude.Settings(opus, DateTimeOffset.UtcNow), Path.Combine(fx.TempRoot, "ai-opus"));
        await LiveClaude.RequireReadyAsync(cli, ct);

        var request = new ModelRequest(
            "You are a connectivity test. Answer only with JSON that matches the schema, with ok set to true.",
            "ping", LiveClaude.OkSchema, opus, LiveClaude.Effort, TimeSpan.FromSeconds(120), 256);
        Assert.True(request.Model == opus, "The request does not carry the opus alias unchanged.");
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
        var inTok = ok.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var outTok = ok.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?";
        fx.Result("claude-opus", $"ok in {AiHtmlChecks.Seconds(ok.Duration)} s, tokens {inTok}/{outTok}");
    }
}

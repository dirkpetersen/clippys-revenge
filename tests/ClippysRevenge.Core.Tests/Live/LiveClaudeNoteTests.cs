using System.Globalization;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// SPEC 25.6: one drafting call whose schema has note_for_user and whose prompts carry the about_me and related_mail blocks.
/// Needs CLIPPY_LIVE=1 and CLIPPY_LIVE_CLAUDE=1. Synthetic input only, no mailbox data, no Outlook. Output holds the
/// duration and the note length, never the note, body text or prompts.
/// </summary>
[Collection(LiveOutlookCollection.Name)]   // for fx.Result and fx.TempRoot only; no COM
[Trait("live", "claude")]
[Trait("os", "windows")]
public sealed class LiveClaudeNoteTests(LiveOutlook fx)
{
    [Fact(Timeout = 180_000)]
    public async Task Draft_call_with_facts_related_mail_and_note_parses()
    {
        var model = LiveClaude.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var cli = LiveClaude.CreateModel(() => LiveClaude.Settings(model, now), Path.Combine(fx.TempRoot, "ai-note"));
        await LiveClaude.RequireReadyAsync(cli, ct);

        var thread = new List<ThreadMessage>
        {
            new("m1", "Inbox", now.AddDays(-1), "Alex", "alex@example.org", "sam.tester@example.org", null,
                "Ski club room", [], "Is the club room free on Friday?"),
        };
        var email = new PromptEmail("Alex", "alex@example.org", "sam.tester@example.org", null, now.AddMinutes(-5),
            "Ski club room", [], "Thanks. Which room should we use, and where do I find the sign-up sheet?");
        var related = new List<RelatedRow>
        {
            new("r1", null, "Sign-up sheet", "Sent Items", now.AddDays(-30), "Sam Tester", "sam.tester@example.org",
                "alex@example.org", null, "Sign-up sheet", "Hi Alex, the sign-up sheet is on the board in room 12. Sam"),
        };
        var input = new DraftPromptInput(now, TimeZoneInfo.Utc, "UTC", "", false, thread, email, null, related);
        const string facts = "I am Sam Tester, treasurer of the ski club. The club room is room 12 in the main hall.";
        var request = new ModelRequest(
            DraftPrompt.SystemPrompt("Sam Tester", "sam.tester@example.org", false, DraftPrompt.NoStyleGuideText,
                facts: facts, related: DraftPrompt.HasRelated(input), reason: true),
            DraftPrompt.UserPrompt(input),
            DraftModel.ReplySchema, model, LiveClaude.Effort, TimeSpan.FromSeconds(120), DraftModel.DraftMaxOutputTokens);

        var result = await cli.RunAsync(request, ct);
        if (result is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
        if (result is ModelResult.NotSignedIn) Assert.Skip(LiveClaude.NotReady(ClaudeCliStatusTexts.NotSignedIn));
        if (result is ModelResult.Failed f)
            Assert.Fail($"The drafting call failed ({f.Kind}, code {f.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
        if (result is not ModelResult.Ok ok)
        {
            Assert.Fail("The drafting call returned an unexpected result.");
            return;
        }

        var reply = DraftReply.Parse(ok.Output);
        Assert.True(reply is not null, "The drafting output doesn't match ReplySchema.");
        Assert.True(reply.Confidence is >= 0.0 and <= 1.0, "The confidence is outside 0..1.");
        Assert.True(reply.NoteForUser is null || reply.NoteForUser.Length <= DraftModel.MaxNoteChars + 1, // Cut adds "…"
            "note_for_user is longer than 160 characters.");
        var noteLen = reply.NoteForUser?.Length.ToString(CultureInfo.InvariantCulture) ?? "none";
        fx.Result("claude-note", $"ok in {AiHtmlChecks.Seconds(ok.Duration)} s, note length {noteLen}, model {model}");
    }
}

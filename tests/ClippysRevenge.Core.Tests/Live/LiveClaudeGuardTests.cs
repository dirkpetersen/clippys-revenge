using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Not live: runs in CI on Windows. The pure helpers and decorators of the live Claude tests (M11.10), over the
/// fakes. No Outlook and no claude process.
/// </summary>
[Trait("os", "windows")]
public sealed class LiveClaudeGuardTests
{
    private const string Token = "clippy-live-test-token";

    private static Func<string, string?> Env(string? live, string? claude, string? model = null) => name => name switch
    {
        "CLIPPY_LIVE" => live,
        "CLIPPY_LIVE_CLAUDE" => claude,
        LiveClaude.ModelVariable => model,
        _ => null,
    };

    // ---- 1-3 gate, model, settings

    [Fact]
    public void Gate_needs_both_variables_set_to_1_on_windows()
    {
        Assert.True(LiveClaude.IsEnabled(Env("1", "1"), true));
        Assert.False(LiveClaude.IsEnabled(Env("1", null), true));
        Assert.False(LiveClaude.IsEnabled(Env(null, "1"), true));
        Assert.False(LiveClaude.IsEnabled(Env("1", "true"), true));
        Assert.False(LiveClaude.IsEnabled(Env("1", "1"), false));
    }

    [Fact]
    public void Model_defaults_to_haiku_and_accepts_only_valid_overrides()
    {
        Assert.Equal("claude-haiku-5-5", LiveClaude.ModelFrom(Env("1", "1")));
        Assert.Equal("claude-haiku-5-5", LiveClaude.ModelFrom(Env("1", "1", "  ")));
        Assert.Equal("claude-sonnet-5", LiveClaude.ModelFrom(Env("1", "1", " claude-sonnet-5 ")));
        Assert.Null(LiveClaude.ModelFrom(Env("1", "1", "bad model!")));
    }

    [Fact]
    public void Settings_are_consented_manual_only_haiku_low_and_leave_the_default_alone()
    {
        var s = LiveClaude.Settings("claude-haiku-5-5", DateTimeOffset.UtcNow);
        Assert.True(s.HasConsent);
        Assert.False(s.Enabled);
        Assert.Equal("claude-haiku-5-5", s.Model);
        Assert.Equal("low", s.Effort);
        Assert.Equal(120, s.TimeoutSeconds);
        Assert.Equal(AiBackend.ClaudeCode, s.Backend);
        Assert.Empty(s.Validate());
        Assert.Equal("claude-opus-5-5", new AiDraftingSettings().Model);
    }

    // ---- 4-8 html checks

    [Theory]
    [InlineData("<p>Hi <span style=\"background-color:#ffff00\">[[date]]</span></p>")]
    [InlineData("<p>a</p><br><ul><li>x</li></ul>")]
    [InlineData("<blockquote><i>q</i></blockquote>")]
    public void Allow_listed_fragments_are_sanitized(string fragment) =>
        Assert.True(AiHtmlChecks.FragmentIsSanitized(fragment));

    [Theory]
    [InlineData("<a href=\"x\">y</a>")]
    [InlineData("<p onclick=\"x\">a</p>")]
    [InlineData("<script>x</script>")]
    [InlineData("<img src=x>")]
    [InlineData("<P>a</P>")]
    [InlineData("<p >a</p>")]
    [InlineData("a < b")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_fragments_are_not_sanitized(string? fragment) =>
        Assert.False(AiHtmlChecks.FragmentIsSanitized(fragment));

    [Fact]
    public void The_real_sanitizer_output_passes_the_check()
    {
        var html = DraftHtml.Sanitize("<p onclick=x>Hi <a href='https://example.org'>link</a><script>z</script> [[date]]</p><img src=x>");
        Assert.True(AiHtmlChecks.FragmentIsSanitized(html));
    }

    [Fact]
    public void Active_counts_links_images_and_scripts_but_not_named_anchors()
    {
        Assert.Equal((1, 1, 1), AiHtmlChecks.Active("<A HREF='x'>a</A><a name=\"_MailEndCompose\"></a><IMG src=y><script>"));
        Assert.Equal((0, 0, 0), AiHtmlChecks.Active(null));
    }

    [Fact]
    public void Start_of_fragment_is_found_in_the_body_ignoring_layout()
    {
        Assert.True(AiHtmlChecks.StartOfFragmentIn("<p>Hello Sam, Thursday works</p>", "Hello Sam,  Thursday works for me\r\n> quoted"));
        Assert.False(AiHtmlChecks.StartOfFragmentIn("<p>Hello Sam, Thursday works</p>", "Something else entirely"));
        Assert.False(AiHtmlChecks.StartOfFragmentIn("<p></p>", "anything"));
    }

    // ---- 9-10 GuardedModel

    private static ModelRequest Request(string model = "M", string effort = "low", string? user = null, string? system = null) =>
        new(system ?? ("You write replies. " + DraftPrompt.NoStyleGuideText),
            user ?? $"<context>\nsignature_added: no\n</context>\n<thread>\n</thread>\n<email_to_answer subject=\"{Token}\">\n</email_to_answer>",
            "{}", model, effort, TimeSpan.FromSeconds(30), 100);

    [Fact]
    public async Task Guard_passes_one_good_request_and_refuses_a_second()
    {
        var fake = new FakeDraftModel();
        fake.Enqueue(FakeDraftModel.Ok("""{"ok":true}"""));
        var guard = new GuardedModel(fake, "M", Token);

        var first = await guard.RunAsync(Request(), TestContext.Current.CancellationToken);
        Assert.IsType<ModelResult.Ok>(first);
        Assert.Equal(1, guard.Calls);
        Assert.Null(guard.Tripped);
        Assert.Same(first, guard.LastResult);
        Assert.NotNull(guard.LastDuration);

        var second = await guard.RunAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(new ModelResult.Failed(ModelFailure.BadInput, null), second);
        Assert.Equal("second call", guard.Tripped);
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("model", "X", "low", null, null)]
    [InlineData("effort", "M", "medium", null, null)]
    [InlineData("token", "M", "low", "<thread>\n</thread>\nno token here signature_added: no", null)]
    [InlineData("thread", "M", "low", $"{Token}\n<thread>\n<message from=\"a\">\n</message>\n</thread>", null)]
    [InlineData("style", "M", "low", null, "STYLE")]
    [InlineData("signature", "M", "low", $"{Token}\nsignature_added: yes\n<thread>\n</thread>", null)]
    public async Task Guard_refuses_each_bad_field_before_the_model_runs(string reason, string model, string effort, string? user, string? system)
    {
        var fake = new FakeDraftModel();
        var guard = new GuardedModel(fake, "M", Token);

        var result = await guard.RunAsync(Request(model, effort, user, system), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelResult.Failed(ModelFailure.BadInput, null), result);
        Assert.Equal(reason, guard.Tripped);
        Assert.Equal(0, guard.Calls);
        Assert.Empty(fake.Requests);
    }

    private static readonly string WithNote =
        "You write replies. " + DraftPrompt.NoStyleGuideText + "\n\n" + DraftPrompt.MeetingNote.Replace("{name}", "Sam");

    [Fact]
    public async Task Guard_passes_a_request_with_the_meeting_note_when_required()
    {
        var fake = new FakeDraftModel();
        fake.Enqueue(FakeDraftModel.Ok("""{"ok":true}"""));
        var guard = new GuardedModel(fake, "M", Token, meetingNote: true);

        await guard.RunAsync(Request(system: WithNote), TestContext.Current.CancellationToken);

        Assert.Null(guard.Tripped);
        Assert.Equal(1, guard.Calls);
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Guard_refuses_a_wrong_meeting_note_before_the_model_runs(bool required, bool promptHasNote)
    {
        var fake = new FakeDraftModel();
        var guard = new GuardedModel(fake, "M", Token, meetingNote: required);

        var result = await guard.RunAsync(Request(system: promptHasNote ? WithNote : null), TestContext.Current.CancellationToken);

        Assert.Equal(new ModelResult.Failed(ModelFailure.BadInput, null), result);
        Assert.Equal("meeting-note", guard.Tripped);
        Assert.Equal(0, guard.Calls);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guard_without_a_meeting_note_check_accepts_both_prompts(bool promptHasNote)
    {
        var fake = new FakeDraftModel();
        fake.Enqueue(FakeDraftModel.Ok("""{"ok":true}"""));
        var guard = new GuardedModel(fake, "M", Token);

        await guard.RunAsync(Request(system: promptHasNote ? WithNote : null), TestContext.Current.CancellationToken);

        Assert.Null(guard.Tripped);
        Assert.Equal(1, guard.Calls);
    }

    // ---- 11 FixedSelectionMailbox

    [Fact]
    public async Task Fixed_selection_never_touches_the_inner_selection_or_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new FakeAiMailbox { Identity = new AiIdentity("Test User", "test@example.org") };
        var selection = new SelectedEmail("E1", "S1");
        var box = new FixedSelectionMailbox(inner, selection);

        Assert.Same(selection, await box.GetSelectedAsync(ct));
        Assert.False(await box.ShowDraftsAsync(ct));
        Assert.Equal(0, inner.TotalCalls);
        Assert.Equal(1, box.SelectionCalls);

        Assert.Null(await box.ReadEmailAsync("E1", ct));
        Assert.Equal(inner.Identity, await box.GetIdentityAsync(WorkPriority.Interactive, ct));
        Assert.Equal(FakeAiMailbox.DefaultFacts, await box.GetFactsAsync("", "E1", WorkPriority.Interactive, ct));
        Assert.Equal(3, inner.TotalCalls);
    }

    // ---- 12-13 RecordingReplyWriter

    [Theory]
    [InlineData("E1", null, DraftMarker.Tool, "marker")]
    [InlineData("E2", null, DraftMarker.Ai, "id")]
    [InlineData("E1", "x@example.org", DraftMarker.Ai, "mailbox")]
    public async Task Writer_refuses_a_plan_that_is_not_the_expected_ai_reply(string id, string? mailbox, string marker, string what)
    {
        var inner = new FakeReplyWriter();
        var writer = new RecordingReplyWriter(inner, "E1", _ => { });
        var plan = new ReplyPlan(id, mailbox, false, "<p>x</p>", false, marker);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ReplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal("Live test refused the reply plan: " + what + ".", ex.Message);
        Assert.Empty(inner.Plans);
        Assert.Empty(writer.Plans);
    }

    [Fact]
    public async Task Writer_passes_a_valid_plan_and_reports_the_draft()
    {
        var inner = new FakeReplyWriter();
        RepliedDraft? seen = null;
        var writer = new RecordingReplyWriter(inner, "E1", d => seen = d);
        var plan = new ReplyPlan("E1", null, false, "<p>x</p>", false, DraftMarker.Ai);

        var made = await writer.ReplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Single(inner.Plans);
        Assert.Equal("D1", made.EntryId);
        Assert.Same(made, seen);
        Assert.Single(writer.Plans);
    }
}

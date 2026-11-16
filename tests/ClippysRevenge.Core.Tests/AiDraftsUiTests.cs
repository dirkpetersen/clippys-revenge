using System.Reflection;
using ClippysRevenge.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AiDraftsUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-aiui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static AiDraftingSettings Ai(bool consent = false, bool enabled = false)
    {
        var a = new AiDraftingSettings();
        if (consent) a.RecordConsent(DateTimeOffset.Parse("2026-11-13T10:00:00Z"));
        if (enabled) a.Enabled = true;
        return a;
    }

    [Theory]
    [InlineData("opus", "latest Opus")]
    [InlineData("sonnet", "latest Sonnet")]
    [InlineData("haiku", "latest Haiku")]
    [InlineData(" opus ", "latest Opus")]
    [InlineData("Opus", "")]
    [InlineData("claude-opus-5-5", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ModelHint_names_the_alias(string? model, string expected) =>
        Assert.Equal(expected, AiTabTexts.ModelHint(model));

    // ---- T1 texts ----

    [Fact]
    public void Consent_text_is_exact()
    {
        Assert.Equal("Clippy will send new emails from your Inbox, earlier messages in the same thread, and samples of your sent mail to Claude (Anthropic) to write reply drafts in your style. Drafts are only saved, never sent. With Claude Code, this uses your Claude plan's usage. Shared mailboxes are included only if you turn them on one by one.", AiConsent.Text);
        Assert.Equal("Turn on AI drafts?", AiConsent.Title);
        Assert.Equal("Turn on", AiConsent.TurnOnButton);
        Assert.Equal("Cancel", AiConsent.CancelButton);
    }

    [Fact]
    public void Today_text()
    {
        Assert.Equal("Today: 3 of 30 Claude calls, 1 draft", AiTabTexts.Today(3, 30, 1));
        Assert.Equal("Today: 0 of 30 Claude calls, 0 drafts", AiTabTexts.Today(0, 30, 0));
        Assert.Equal("Today: 1,200 of 500 Claude calls, 2 drafts", AiTabTexts.Today(1200, 500, 2));
    }

    [Fact]
    public void PendingCheckNote_compares_backend_model_and_path()
    {
        var live = new AiDraftingSettings();
        Assert.Equal("", AiTabTexts.PendingCheckNote(new AiDraftingSettings(), live));
        Assert.Equal(AiTabTexts.CheckNote, AiTabTexts.PendingCheckNote(new AiDraftingSettings { Backend = AiBackend.ApiKey }, live));
        Assert.Equal(AiTabTexts.CheckNote,
            AiTabTexts.PendingCheckNote(new AiDraftingSettings { Model = "x" }, new AiDraftingSettings { Model = "X" }));
        Assert.Equal("", AiTabTexts.PendingCheckNote(new AiDraftingSettings { ClaudeCliPath = @" C:\a\claude.exe" },
            new AiDraftingSettings { ClaudeCliPath = @"c:\A\claude.exe" }));
        Assert.Throws<ArgumentNullException>(() => AiTabTexts.PendingCheckNote(null!, live));
        Assert.Throws<ArgumentNullException>(() => AiTabTexts.PendingCheckNote(live, null!));
    }

    [Fact]
    public void StyleResult_texts()
    {
        Assert.Equal("Writing style updated from 150 sent emails.", AiTabTexts.StyleResult(new StyleRunResult.Updated(150, TimeSpan.FromSeconds(41))));
        Assert.Equal("Only 7 sent emails found. Clippy needs 20.", AiTabTexts.StyleResult(new StyleRunResult.TooFewEmails(7)));
        Assert.Equal("Writing style not updated: Claude isn't signed in.", AiTabTexts.StyleResult(new StyleRunResult.Failed("Claude isn't signed in")));
        Assert.Equal(AiDraftingSettings.ConsentRequiredMessage, AiTabTexts.StyleResult(new StyleRunResult.NoConsent()));
        Assert.Equal("Your writing style is already being updated.", AiTabTexts.StyleResult(new StyleRunResult.Busy()));
        Assert.Equal("Your writing style was kept.", AiTabTexts.StyleResult(new StyleRunResult.Edited()));
        Assert.Equal("Your writing style was kept.", AiTabTexts.StyleResult(new StyleRunResult.NotDue()));
        Assert.Throws<ArgumentNullException>(() => AiTabTexts.StyleResult(null!));
    }

    [Fact]
    public void ErrorText_names_the_sender_entry()
    {
        var d = new AiDraftingSettings { ExcludeSenders = ["a@example.org", "bad\u0001 entry"] };
        Assert.Equal("\"bad entry\": Must be…",
            AiTabTexts.ErrorText(new SettingsError("AiDrafting.ExcludeSenders[1]", "Must be…"), d));
        Assert.Equal("Must be…", AiTabTexts.ErrorText(new SettingsError("AiDrafting.ExcludeSenders[5]", "Must be…"), d));
        Assert.Equal("Must be…", AiTabTexts.ErrorText(new SettingsError("AiDrafting.ExcludeSenders[1]", "Must be…"), null));
        Assert.Equal("Must be…", AiTabTexts.ErrorText(new SettingsError("AiDrafting.Model", "Must be…"), d));
        var inc = new AiDraftingSettings { IncludeOnlySenders = [new string('x', 100)] };
        var text = AiTabTexts.ErrorText(new SettingsError("AiDrafting.IncludeOnlySenders[0]", "m"), inc);
        Assert.Equal("\"" + new string('x', 59) + "…\": m", text);
    }

    [Fact]
    public void Small_texts()
    {
        Assert.Equal("Couldn't save the key: x", AiTabTexts.KeySaveFailed("x"));
        Assert.Equal("Couldn't open the file: y", AiTabTexts.OpenFailed("y"));
        Assert.Equal("Couldn't change AI drafts: z", AiMenuTexts.ToggleFailed("z"));
    }

    [Fact]
    public void No_text_constant_mentions_a_sign_in()
    {
        foreach (var t in new[] { typeof(AiConsent), typeof(AiMenuTexts), typeof(AiTabTexts) })
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                var v = (string)f.GetRawConstantValue()!;
                foreach (var bad in new[] { "login", "claude.ai", "oauth", "token" })
                    Assert.False(v.Contains(bad, StringComparison.OrdinalIgnoreCase), $"{t.Name}.{f.Name} contains {bad}");
            }
    }

    // ---- T2 menu and toggle ----

    [Fact]
    public void Menu_defaults()
    {
        var m = AiDraftsMenu.Build(Ai(), available: true, outlookUnavailable: false, busy: false);
        Assert.Equal(new AiDraftsMenuModel(false, true, true, true, true, DontDraftEnabled: true), m);
    }

    [Fact]
    public void Menu_checked_needs_enabled_and_consent()
    {
        Assert.True(AiDraftsMenu.Build(Ai(consent: true, enabled: true), true, false, false).AutoChecked);
        Assert.False(AiDraftsMenu.Build(Ai(consent: false, enabled: true), true, false, false).AutoChecked);
    }

    [Fact]
    public void Menu_unavailable_services_disable_the_items()
    {
        var m = AiDraftsMenu.Build(Ai(), available: false, outlookUnavailable: false, busy: false);
        Assert.False(m.AutoEnabled);
        Assert.False(m.DraftSelectedEnabled);
        Assert.False(m.ShowDraftsEnabled);
        Assert.True(m.ShowDraftsVisible);
    }

    [Fact]
    public void Menu_outlook_unavailable()
    {
        var m = AiDraftsMenu.Build(Ai(), true, outlookUnavailable: true, busy: false);
        Assert.False(m.ShowDraftsVisible);
        Assert.False(m.DraftSelectedEnabled);
        Assert.True(m.AutoEnabled);
    }

    [Fact]
    public void Menu_busy_disables_only_auto()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, busy: true);
        Assert.Equal(new AiDraftsMenuModel(false, false, true, true, true, DontDraftEnabled: true), m);
    }

    [Fact]
    public void Menu_rescan_defaults()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, false);
        Assert.Equal("Rescan emails from the last 24 hours…", m.RescanText);
        Assert.True(m.RescanEnabled);
        Assert.False(m.RescanCancels);
    }

    [Fact]
    public void Menu_rescan_running()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, false, new RescanProgress(3, 12));
        Assert.Equal("Cancel rescan (3/12)", m.RescanText);
        Assert.True(m.RescanEnabled);
        Assert.True(m.RescanCancels);
        Assert.False(m.DraftSelectedEnabled);
        Assert.True(m.AutoEnabled);
        Assert.True(m.ShowDraftsEnabled);
    }

    [Fact]
    public void Menu_rescan_running_while_outlook_unavailable()
    {
        var m = AiDraftsMenu.Build(Ai(), true, outlookUnavailable: true, busy: false, rescan: new RescanProgress(0, 0));
        Assert.Equal("Cancel rescan (0/0)", m.RescanText);
        Assert.True(m.RescanEnabled);
        Assert.True(m.RescanCancels);
    }

    [Fact]
    public void Menu_rescan_starting()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, false, rescan: null, rescanStarting: true);
        Assert.Equal(RescanTexts.MenuItem, m.RescanText);
        Assert.False(m.RescanEnabled);
        Assert.False(m.RescanCancels);
        Assert.False(m.DraftSelectedEnabled);
    }

    [Fact]
    public void Menu_rescan_running_wins_over_starting()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, false, new RescanProgress(1, 2), rescanStarting: true);
        Assert.True(m.RescanCancels);
        Assert.True(m.RescanEnabled);
    }

    [Fact]
    public void Menu_rescan_idle_unavailable_cases()
    {
        var m = AiDraftsMenu.Build(Ai(), true, outlookUnavailable: true, busy: false);
        Assert.False(m.RescanEnabled);
        Assert.Equal(RescanTexts.MenuItem, m.RescanText);
        var n = AiDraftsMenu.Build(Ai(), available: false, outlookUnavailable: false, busy: false, rescan: new RescanProgress(1, 2));
        Assert.False(n.RescanEnabled);
        Assert.False(n.RescanCancels);
        Assert.Equal(RescanTexts.MenuItem, n.RescanText);
    }

    [Fact]
    public void Menu_busy_does_not_touch_rescan()
    {
        var m = AiDraftsMenu.Build(Ai(), true, false, busy: true);
        Assert.True(m.RescanEnabled);
        Assert.False(m.AutoEnabled);
    }

    [Fact]
    public void PlanBalloon_cases()
    {
        Assert.Equal("There are no emails from the last 24 hours to rescan.", RescanFlow.PlanBalloon(new RescanPlan(0, 30, 30, true)));
        Assert.Equal("There are no emails from the last 24 hours to rescan.", RescanFlow.PlanBalloon(new RescanPlan(0, 30, 0, true)));
        Assert.Equal("Today's AI draft limit (30) is used up.", RescanFlow.PlanBalloon(new RescanPlan(5, 30, 0, true)));
        Assert.Null(RescanFlow.PlanBalloon(new RescanPlan(5, 30, 3, false)));
        Assert.Null(RescanFlow.PlanBalloon(new RescanPlan(1, 1, 1, true)));
        Assert.Throws<ArgumentNullException>(() => RescanFlow.PlanBalloon(null!));
    }

    private static RescanResult Result(bool started, int d, int u, int m, string msg) =>
        new(started, null, 5, new RescanCounts(5, d, u, m, 0, 0, 0, 0, 0), 1, TimeSpan.Zero, msg);

    [Fact]
    public void ResultBalloon_cases()
    {
        Assert.Null(RescanFlow.ResultBalloon(Result(false, 0, 0, 0, "")));
        Assert.Equal(new Balloon(AiDraftingSettings.ConsentRequiredMessage, BalloonKind.Info, BalloonAction.None),
            RescanFlow.ResultBalloon(Result(false, 0, 0, 0, AiDraftingSettings.ConsentRequiredMessage)));
        Assert.Equal(new Balloon("Rescan: 5 emails, 1 draft (0 s)", BalloonKind.Info, BalloonAction.ShowDrafts),
            RescanFlow.ResultBalloon(Result(true, 1, 0, 0, "Rescan: 5 emails, 1 draft (0 s)")));
        Assert.Equal(BalloonAction.ShowDrafts, RescanFlow.ResultBalloon(Result(true, 0, 1, 0, "x"))!.Action);
        Assert.Equal(BalloonAction.None, RescanFlow.ResultBalloon(Result(true, 0, 0, 1, "x"))!.Action);
        var stopped = "Rescan stopped (cancelled) after 0 of 5 emails: 0 drafts (0 s)";
        Assert.Equal(new Balloon(stopped, BalloonKind.Info, BalloonAction.None), RescanFlow.ResultBalloon(Result(true, 0, 0, 0, stopped)));
        Assert.Throws<ArgumentNullException>(() => RescanFlow.ResultBalloon(null!));
    }

    [Fact]
    public void Rescan_texts()
    {
        Assert.Equal("Update or remove my AI drafts when a thread moves on", AiTabTexts.ReviseDraftsText);
        Assert.Equal("I couldn't start the rescan: x", RescanTexts.StartFailed("x"));
    }

    [Fact]
    public void Toggle_steps()
    {
        Assert.Equal(AiToggleStep.TurnOff, AiToggle.Next(Ai(consent: true, enabled: true)));
        Assert.Equal(AiToggleStep.AskConsent, AiToggle.Next(Ai()));
        Assert.Equal(AiToggleStep.CheckBackend, AiToggle.Next(Ai(consent: true)));
        Assert.Equal(AiToggleStep.TurnOff, AiToggle.Next(Ai(enabled: true)));
        Assert.Equal(AiToggleOutcome.TurnOn, AiToggle.AfterCheck(new ModelStatus(true, "✓")));
        Assert.Equal(AiToggleOutcome.OpenSettings, AiToggle.AfterCheck(new ModelStatus(false, "✗")));
    }

    [Fact]
    public void Null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => AiDraftsMenu.Build(null!, true, false, false));
        Assert.Throws<ArgumentNullException>(() => AiToggle.Next(null!));
        Assert.Throws<ArgumentNullException>(() => AiToggle.AfterCheck(null!));
    }

    // ---- T7 lines ----

    [Fact]
    public void Lines_parse_and_join()
    {
        Assert.Equal(["a@x.org", "example.org", "@y.org"], AiSettingsLines.Parse(" a@x.org\r\n\r\nexample.org \n\r @y.org\n"));
        Assert.Empty(AiSettingsLines.Parse(null));
        Assert.Equal("a\r\nb", AiSettingsLines.Join(["a", "b"]));
        Assert.Equal("", AiSettingsLines.Join(null));
        Assert.Equal("a\r\nb", AiSettingsLines.Join(["a", null, "b"]));
        List<string> x = ["a@x.org", "a@x.org", "b.org"];
        Assert.Equal(x, AiSettingsLines.Parse(AiSettingsLines.Join(x)));
    }

    // ---- T11 fields ----

    [Theory]
    [InlineData("AiDrafting.Enabled", AiTabField.Enabled)]
    [InlineData("AiDrafting.MeetingTimes", AiTabField.MeetingTimes)]
    [InlineData("AiDrafting.MeetingTimeOptions", AiTabField.MeetingTimeOptions)]
    [InlineData("AiDrafting.ExcludeSenders[0]", AiTabField.ExcludeSenders)]
    [InlineData("AiDrafting.ExcludeSenders", AiTabField.ExcludeSenders)]
    [InlineData("AiDrafting.ClaudeCliPath", AiTabField.ClaudeCliPath)]
    [InlineData("AiDrafting.TimeoutSeconds", AiTabField.None)]
    [InlineData("AiDrafting.None", AiTabField.None)]
    [InlineData("AiDrafting.enabled", AiTabField.None)]
    [InlineData("AiDrafting.1", AiTabField.None)]
    [InlineData("Port", AiTabField.None)]
    [InlineData("", AiTabField.None)]
    [InlineData(null, AiTabField.None)]
    public void Tab_field_mapping(string? field, AiTabField expected) => Assert.Equal(expected, AiTabFields.For(field));

    // ---- T13 editor target ----

    [Fact]
    public void Editor_target()
    {
        var paths = StylePaths.Default(Path.Combine(_dir, "profile"), Path.Combine(_dir, "appdata"));
        var asked = new List<string>();
        var ok = StyleEditorTarget.Resolve(paths, p => { asked.Add(p); return true; });
        Assert.Equal(new EditorTarget(paths.UserSkillFile, null), ok);
        Assert.Equal([paths.UserSkillFile], asked);

        Assert.Equal(new EditorTarget(null, AiTabTexts.NoSkillYet), StyleEditorTarget.Resolve(paths, _ => false));

        var other = paths with { UserSkillFile = Path.Combine(_dir, "notes.txt") };
        Assert.Equal(new EditorTarget(null, AiTabTexts.NoSkillYet), StyleEditorTarget.Resolve(other, _ => true));
        Assert.Throws<ArgumentNullException>(() => StyleEditorTarget.Resolve(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => StyleEditorTarget.Resolve(paths, null!));
    }

    // ---- T10 owner ----

    private sealed class OwnerMailbox : IAiMailbox
    {
        public AiIdentity Identity { get; set; } = new("Dirk Example", "me@example.org");
        public Exception? Throw { get; set; }
        public List<WorkPriority> Priorities { get; } = [];

        public Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct)
        {
            Priorities.Add(priority);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Identity);
        }

        public Task<CandidateFacts?> GetFactsAsync(string mailbox, string entryId, WorkPriority priority, CancellationToken ct) => throw new NotSupportedException();
        public Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<LiveEmail?> ReadEmailAsync(string entryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ShowDraftsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AiDraftInfo?> ReadAiDraftAsync(string mailbox, string draftEntryId, WorkPriority priority, CancellationToken ct) => throw new NotSupportedException();
        public Task<ObsoleteDraftMove> MoveObsoleteAiDraftAsync(string mailbox, string draftEntryId, string expectedFingerprint, WorkPriority priority, CancellationToken ct) => throw new NotSupportedException();
    }

    private MailboxIndexes Indexes(string? smtpMeta)
    {
        var ix = new MailboxIndexes(_dir, []);
        if (smtpMeta is not null) ix.For("").SetMeta("mailbox_smtp", smtpMeta);
        return ix;
    }

    [Fact]
    public async Task Owner_from_identity_records_the_priority()
    {
        var mb = new OwnerMailbox();
        using var ix = Indexes(null);
        var o = await AiOwner.ResolveAsync(mb, ix, WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(new Recipient("Dirk Example", "me@example.org"), o);
        Assert.Equal([WorkPriority.Interactive], mb.Priorities);
    }

    [Fact]
    public async Task Owner_falls_back_to_the_index_address()
    {
        var mb = new OwnerMailbox { Identity = new AiIdentity("Dirk", "") };
        using var ix = Indexes(" me@example.org ");
        Assert.Equal(new Recipient("Dirk", "me@example.org"),
            await AiOwner.ResolveAsync(mb, ix, WorkPriority.Background, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owner_survives_an_unavailable_outlook()
    {
        var mb = new OwnerMailbox { Throw = new OutlookUnavailableException() };
        using var ix = Indexes("me@example.org");
        Assert.Equal(new Recipient("", "me@example.org"),
            await AiOwner.ResolveAsync(mb, ix, WorkPriority.Background, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owner_is_null_when_unknown_or_invalid()
    {
        var mb = new OwnerMailbox { Identity = new AiIdentity("", "") };
        using (var ix = Indexes(null))
            Assert.Null(await AiOwner.ResolveAsync(mb, ix, WorkPriority.Background, TestContext.Current.CancellationToken));
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
        using (var ix = Indexes("x"))
            Assert.Null(await AiOwner.ResolveAsync(mb, ix, WorkPriority.Background, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owner_cancelled()
    {
        using var ix = Indexes("me@example.org");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AiOwner.ResolveAsync(new OwnerMailbox(), ix, WorkPriority.Background, cts.Token));
    }

    [Fact]
    public void DontDraftEnabled_follows_draft_selected()
    {
        Assert.True(AiDraftsMenu.Build(Ai(), true, false, false).DontDraftEnabled);
        Assert.True(AiDraftsMenu.Build(Ai(), true, false, busy: true).DontDraftEnabled);
        Assert.False(AiDraftsMenu.Build(Ai(), true, false, false, new RescanProgress(1, 5)).DontDraftEnabled);
        Assert.False(AiDraftsMenu.Build(Ai(), true, false, false, rescanStarting: true).DontDraftEnabled);
        Assert.False(AiDraftsMenu.Build(Ai(), true, outlookUnavailable: true, busy: false).DontDraftEnabled);
        Assert.False(AiDraftsMenu.Build(Ai(), available: false, outlookUnavailable: false, busy: false).DontDraftEnabled);
    }}

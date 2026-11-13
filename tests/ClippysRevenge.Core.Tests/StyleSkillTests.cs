using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class StyleSkillTests : IDisposable
{
    private static readonly DateTimeOffset T0 = ManualTimeProvider.T0;
    private static readonly Recipient Owner = new("Dirk \"DP\" Petersen", "dirk@example.org");
    private const string BodyMark = "FAKEBODYMARK";

    private sealed class FakeModel : IDraftModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public Queue<ModelResult> Queue { get; } = new();
        public Func<Task>? Hook { get; set; }

        public Task<ModelStatus> CheckAsync(CancellationToken ct) => Task.FromResult(new ModelStatus(true, "ok"));

        public async Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Hook is not null) await Hook();
            return Queue.Count > 0 ? Queue.Dequeue() : OkBody();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    private static string ValidBody() =>
        "## Greetings and sign-offs\n" + BodyMark + " Hi Ann, short and warm. " + string.Concat(Enumerable.Repeat("plain words here ", 15));

    private static ModelResult OkBody(string? body = null) =>
        new ModelResult.Ok(JsonDocument.Parse(JsonSerializer.Serialize(new { body_markdown = body ?? ValidBody() })).RootElement.Clone(),
            TimeSpan.FromSeconds(1), null, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-styleskill-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeModel _model = new();
    private readonly CapturingLogger _log = new();
    private readonly AiDraftingSettings _settings = new()
    {
        Enabled = true, AutoRegenerateStyle = true, ConsentedAtUtc = T0, Model = "claude-sonnet-5", Effort = "high",
    };
    private readonly EmailIndex _ix;
    private readonly StyleSkillFiles _files;
    private readonly StyleSkill _skill;
    private IReadOnlyList<string> _sigs = [];
    private Func<EmailIndex> _indexFactory;

    public StyleSkillTests()
    {
        Directory.CreateDirectory(_root);
        _hub = new ActivityHub(_time);
        _ix = new EmailIndex(Path.Combine(_root, "index.db"));
        _indexFactory = () => _ix;
        _files = new StyleSkillFiles(StylePaths.Default(Path.Combine(_root, "home"), Path.Combine(_root, "appdata")));
        _skill = new StyleSkill(_files, () => _indexFactory(), () => _model, () => _settings, () => _sigs, _hub, _time, _log);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _hub.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int _seq;

    private void Add(int count, string prefix = "ROWW", double firstDaysAgo = 1, double step = 1, string domain = "other.org",
        string folder = "Sent Items", string? subject = "Hi", string? marker = null, string? irt = null, string tail = "")
    {
        var list = new List<IndexedEmail>();
        for (int i = 0; i < count; i++)
        {
            int n = ++_seq;
            list.Add(new IndexedEmail
            {
                EntryId = "E" + n,
                Folder = folder,
                Subject = subject,
                To = $"x{n}@{domain}",
                EventTime = T0.AddDays(-(firstDaysAgo + i * step)),
                Body = $"{prefix}{n:00} thanks for the note, I will look at it soon.{tail}",
                ClippyMarker = marker,
                InReplyTo = irt,
            });
        }
        _ix.UpsertBatch(list);
    }

    private string UserFile => _files.Paths.UserSkillFile;

    private int Samples(ModelRequest r) => Regex.Matches(r.UserPrompt, "<sample ").Count;

    private List<ActivityEvent> AiEvents() => _hub.GetEvents().Where(e => e.Kind == ActivityKind.Ai).ToList();

    [Fact]
    public async Task Regenerate_writes_the_skill()
    {
        Add(30);
        var r = await _skill.RegenerateAsync(Owner, false, Ct);
        Assert.Equal(30, Assert.IsType<StyleRunResult.Updated>(r).SampleCount);

        var req = Assert.Single(_model.Requests);
        Assert.Equal(StylePrompt.System, req.SystemPrompt);
        Assert.Equal(DraftModel.StyleSchema, req.JsonSchema);
        Assert.Equal(TimeSpan.FromSeconds(600), req.Timeout);
        Assert.Equal(16_000, req.MaxOutputTokens);
        Assert.Equal("claude-sonnet-5", req.Model);
        Assert.Equal("high", req.Effort);
        Assert.Equal(30, Samples(req));
        Assert.DoesNotContain("@example.org", req.UserPrompt);
        Assert.DoesNotContain("@other.org", req.UserPrompt);

        Assert.Equal(File.ReadAllText(UserFile), File.ReadAllText(_files.Paths.AppCopyFile));
        var text = File.ReadAllText(UserFile);
        Assert.Contains("Dirk DP Petersen's email writing style", text);
        Assert.Contains("on 2026-06-15 from 30 sent emails", text);
        var ev = Assert.Single(AiEvents());
        Assert.StartsWith("writing style updated from 30 sent emails (", ev.Summary);
        Assert.NotNull(ev.Duration);
        Assert.False(_files.IsEdited());
        Assert.Equal(StyleStatusKind.Generated, _skill.GetStatus().Kind);
    }

    [Fact]
    public async Task AI_originated_mail_is_left_out()
    {
        Add(30);
        Add(1, "AIMARK", marker: "ai");
        Add(1, "TOOLMARK", marker: "tool");
        _ix.UpsertDecision(new AiDecision("src1", "<src>", null, T0.AddDays(-5), AiOutcome.Drafted, DraftCreated: T0.AddDays(-5)));
        Add(1, "REPLYMARK", irt: "<src>", firstDaysAgo: 2);
        await _skill.RegenerateAsync(Owner, false, Ct);
        var p = Assert.Single(_model.Requests).UserPrompt;
        Assert.DoesNotContain("AIMARK", p);
        Assert.DoesNotContain("TOOLMARK", p);
        Assert.DoesNotContain("REPLYMARK", p);
    }

    [Fact]
    public async Task Other_folders_are_left_out()
    {
        Add(30);
        Add(3, "INBOXMARK", folder: "Inbox");
        await _skill.RegenerateAsync(Owner, false, Ct);
        Assert.DoesNotContain("INBOXMARK", Assert.Single(_model.Requests).UserPrompt);
    }

    [Fact]
    public async Task Too_few_emails_makes_no_call()
    {
        Add(19);
        var r = await _skill.RegenerateAsync(Owner, false, Ct);
        Assert.Equal(19, Assert.IsType<StyleRunResult.TooFewEmails>(r).Found);
        Assert.Empty(_model.Requests);
        Assert.Equal("writing style not updated: only 19 sent emails (need 20)", Assert.Single(AiEvents()).Summary);
        Assert.False(File.Exists(UserFile));
        Assert.False(File.Exists(_files.Paths.AppCopyFile));
    }

    [Fact]
    public async Task Regenerate_needs_consent_but_not_enabled()
    {
        Add(30);
        _settings.ConsentedAtUtc = null;
        Assert.IsType<StyleRunResult.NoConsent>(await _skill.RegenerateAsync(Owner, false, Ct));
        Assert.Empty(_model.Requests);
        Assert.Empty(AiEvents());

        _settings.ConsentedAtUtc = T0;
        _settings.Enabled = false;
        Assert.IsType<StyleRunResult.Updated>(await _skill.RegenerateAsync(Owner, false, Ct));
    }

    [Fact]
    public async Task Model_failures_become_events_and_write_nothing()
    {
        Add(30);
        var cases = new (ModelResult Result, string Reason)[]
        {
            (new ModelResult.RateLimited(null), "Claude's usage limit was reached"),
            (new ModelResult.NotSignedIn(), "Claude isn't signed in"),
            (new ModelResult.Failed(ModelFailure.Timeout, null), "a Claude call failed (Timeout)"),
            (OkBody("short"), "Claude's answer wasn't usable"),
        };
        int events = 0;
        foreach (var (result, reason) in cases)
        {
            _model.Queue.Enqueue(result);
            var r = await _skill.RegenerateAsync(Owner, false, Ct);
            Assert.Equal(reason, Assert.IsType<StyleRunResult.Failed>(r).Reason);
            Assert.Equal(++events, AiEvents().Count);
            Assert.Equal("writing style not updated: " + reason, AiEvents()[^1].Summary);
            Assert.False(File.Exists(UserFile));
        }
    }

    [Fact]
    public async Task Cancellation_is_silent()
    {
        Add(30);
        using var cts = new CancellationTokenSource();
        _model.Hook = () =>
        {
            cts.Cancel();
            _model.Queue.Enqueue(new ModelResult.Failed(ModelFailure.Cancelled, null));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _skill.RegenerateAsync(Owner, false, cts.Token));
        Assert.Empty(AiEvents());
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task Edited_file_is_kept_unless_replaced()
    {
        Add(30);
        await _skill.RegenerateAsync(Owner, false, Ct);
        File.AppendAllText(UserFile, "my edit");
        var before = File.ReadAllText(UserFile);
        int events = AiEvents().Count;
        int calls = _model.Requests.Count;

        Assert.IsType<StyleRunResult.Edited>(await _skill.RegenerateAsync(Owner, false, Ct));
        Assert.Equal(calls, _model.Requests.Count);
        Assert.Equal(events, AiEvents().Count);
        Assert.Equal(before, File.ReadAllText(UserFile));
        Assert.Equal(StyleStatusKind.Edited, _skill.GetStatus().Kind);

        Assert.IsType<StyleRunResult.Updated>(await _skill.RegenerateAsync(Owner, true, Ct));
        Assert.False(_files.IsEdited());
        Assert.DoesNotContain("my edit", File.ReadAllText(UserFile));
    }

    [Fact]
    public async Task Edit_during_the_call()
    {
        Add(30);
        void Edit() => Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        _model.Hook = () =>
        {
            Edit();
            File.WriteAllText(UserFile, "USER TEXT");
            return Task.CompletedTask;
        };
        Assert.IsType<StyleRunResult.Edited>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal("USER TEXT", File.ReadAllText(UserFile));
        Assert.Equal(StyleTexts.EditedEvent, Assert.Single(AiEvents()).Summary);

        File.Delete(UserFile);
        File.WriteAllText(UserFile, "USER TEXT");
        Assert.IsType<StyleRunResult.Updated>(await _skill.RegenerateAsync(Owner, true, Ct));
        Assert.Contains("Generated by Clippy's Revenge", File.ReadAllText(UserFile));
    }

    [Fact]
    public async Task Second_run_is_busy_while_one_runs()
    {
        Add(30);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _model.Hook = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = _skill.RegenerateAsync(Owner, false, Ct);
        await entered.Task;
        Assert.IsType<StyleRunResult.Busy>(await _skill.RegenerateAsync(Owner, false, Ct));
        Assert.IsType<StyleRunResult.Busy>(await _skill.RunIfDueAsync(Owner, Ct));
        release.SetResult();
        Assert.IsType<StyleRunResult.Updated>(await first);
    }

    [Fact]
    public async Task Automatic_runs_are_gated_by_settings()
    {
        Add(30);
        _settings.Enabled = false;
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        _settings.Enabled = true;
        _settings.AutoRegenerateStyle = false;
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        _settings.AutoRegenerateStyle = true;
        _settings.ConsentedAtUtc = null;
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Automatic_first_run_when_no_skill()
    {
        Add(30);
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.True(File.Exists(UserFile));
    }

    [Fact]
    public async Task Automatic_weekly_refresh_needs_enough_new_mail()
    {
        Add(30, firstDaysAgo: 10);
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
        int calls = _model.Requests.Count;

        // Newer than the newest sent row (10 days before T0) and before the future "now".
        Add(19, "NEWW", firstDaysAgo: 1, step: 0.001);
        Add(5, "AUTO", firstDaysAgo: 1, step: 0.001, subject: "Automatic reply: x");

        _time.Advance(new TimeSpan(6, 23, 0, 0));
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        _time.Advance(TimeSpan.FromHours(1));      // 7 days: 19 eligible new rows
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        Add(1, "ONEMORE", firstDaysAgo: 1.5);
        _time.Advance(TimeSpan.FromMinutes(30));   // still inside the hourly check
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(calls, _model.Requests.Count);
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(calls + 1, _model.Requests.Count);
    }

    [Fact]
    public async Task Automatic_reports_each_distinct_edit_once()
    {
        Add(30);
        await _skill.RegenerateAsync(Owner, false, Ct);
        File.AppendAllText(UserFile, "edit one");
        Assert.IsType<StyleRunResult.Edited>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(1, AiEvents().Count(e => e.Summary == StyleTexts.EditedEvent));
        Assert.IsType<StyleRunResult.Edited>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(1, AiEvents().Count(e => e.Summary == StyleTexts.EditedEvent));
        File.AppendAllText(UserFile, "edit two");
        Assert.IsType<StyleRunResult.Edited>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(2, AiEvents().Count(e => e.Summary == StyleTexts.EditedEvent));
    }

    [Fact]
    public async Task Automatic_failure_backs_off_for_24_hours()
    {
        Add(30);
        _model.Queue.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));
        Assert.IsType<StyleRunResult.Failed>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Single(_model.Requests);
        _time.Advance(TimeSpan.FromHours(23));
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Single(_model.Requests);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
        Assert.Equal(2, _model.Requests.Count);
    }

    private sealed class ThrowingList : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new InvalidOperationException("boom");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("boom");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task Automatic_corpus_exception_records_failure_and_backs_off_for_24_hours()
    {
        Add(30);
        _sigs = new ThrowingList();
        var r = await _skill.RunIfDueAsync(Owner, Ct);
        Assert.Equal(StyleTexts.CorpusFailed, Assert.IsType<StyleRunResult.Failed>(r).Reason);
        Assert.Single(AiEvents());
        Assert.Empty(_model.Requests);
        _sigs = [];
        _time.Advance(TimeSpan.FromHours(23));
        Assert.IsType<StyleRunResult.NotDue>(await _skill.RunIfDueAsync(Owner, Ct));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
    }

    [Fact]
    public async Task Automatic_runs_with_only_an_app_copy()
    {
        Add(30);
        Directory.CreateDirectory(Path.GetDirectoryName(_files.Paths.AppCopyFile)!);
        File.WriteAllText(_files.Paths.AppCopyFile, "OLD APP COPY");
        Assert.IsType<StyleRunResult.Updated>(await _skill.RunIfDueAsync(Owner, Ct));
    }

    [Fact]
    public async Task ReadBodyForPrompt_never_waits_for_a_run()
    {
        Assert.Equal(StyleSkillText.NoStyleGuideLine, _skill.ReadBodyForPrompt());
        Add(30);
        string? during = null;
        _model.Hook = () =>
        {
            during = _skill.ReadBodyForPrompt();
            return Task.CompletedTask;
        };
        await _skill.RegenerateAsync(Owner, false, Ct);
        Assert.Equal(StyleSkillText.NoStyleGuideLine, during);
        var body = _skill.ReadBodyForPrompt();
        Assert.StartsWith("## Greetings and sign-offs", body);
        Assert.Contains(BodyMark, body);
    }

    [Fact]
    public async Task Index_failure_is_reported()
    {
        _indexFactory = () => throw new InvalidOperationException("boom");
        var r = await _skill.RegenerateAsync(Owner, false, Ct);
        Assert.Equal("couldn't read the index", Assert.IsType<StyleRunResult.Failed>(r).Reason);
        Assert.Single(AiEvents());
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Signatures_are_cut_from_the_samples()
    {
        _sigs = ["Dirk Petersen\nResearch Computing"];
        Add(30, tail: "\n\nDirk Petersen\nResearch Computing");
        await _skill.RegenerateAsync(Owner, false, Ct);
        var p = Assert.Single(_model.Requests).UserPrompt;
        Assert.DoesNotContain("Research Computing", p);
        Assert.Equal(30, Samples(_model.Requests[0]));
    }

    [Fact]
    public async Task Logs_and_events_never_carry_mail_text()
    {
        Add(30);
        await _skill.RegenerateAsync(Owner, false, Ct);                                  // success
        _model.Queue.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));
        await _skill.RegenerateAsync(Owner, true, Ct);                                   // failure
        _model.Hook = () =>
        {
            File.WriteAllText(UserFile, "USER TEXT");
            return Task.CompletedTask;
        };
        await _skill.RunIfDueAsync(Owner, Ct);                                           // edit path
        _model.Hook = null;

        var other = new StyleSkillTests();
        try
        {
            other.Add(19);
            await other._skill.RegenerateAsync(Owner, false, Ct);                        // too few
            Check(other._log.Lines, other.AiEvents());
        }
        finally { other.Dispose(); }
        Check(_log.Lines, AiEvents());

        static void Check(List<string> lines, List<ActivityEvent> events)
        {
            Assert.NotEmpty(lines);
            foreach (var bad in new[] { "ROWW", "Petersen", "body_markdown", "<sample", BodyMark })
            {
                Assert.DoesNotContain(lines, l => l.Contains(bad, StringComparison.Ordinal));
                Assert.DoesNotContain(events, e => e.Summary.Contains(bad, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task Owner_is_validated()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _skill.RunIfDueAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _skill.RunIfDueAsync(new Recipient("x", "dirk"), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _skill.RegenerateAsync(null!, false, Ct));
    }
}

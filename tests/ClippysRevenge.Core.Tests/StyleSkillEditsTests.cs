using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class StyleSkillEditsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = ManualTimeProvider.T0;
    private static readonly Recipient Owner = new("Dirk", "dirk@example.org");

    private sealed class FakeModel : IDraftModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelStatus> CheckAsync(CancellationToken ct) => Task.FromResult(new ModelStatus(true, "ok"));

        public Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            var body = "## Greetings and sign-offs\nHi Ann, short and warm. " + string.Concat(Enumerable.Repeat("plain words here ", 15));
            var json = JsonSerializer.Serialize(new { body_markdown = body });
            return Task.FromResult<ModelResult>(new ModelResult.Ok(JsonDocument.Parse(json).RootElement.Clone(), TimeSpan.FromSeconds(1), null, null));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-styleedits-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeModel _model = new();
    private readonly CapturingLogger _log = new();
    private readonly AiDraftingSettings _settings = new()
    {
        Enabled = true, AutoRegenerateStyle = true, ConsentedAtUtc = T0, Model = "claude-sonnet-5", Effort = "high",
    };
    private readonly EmailIndex _ix;
    private readonly StyleSkill _skill;
    private int _seq;

    public StyleSkillEditsTests()
    {
        Directory.CreateDirectory(_root);
        _hub = new ActivityHub(_time);
        _ix = new EmailIndex(Path.Combine(_root, "index.db"));
        var files = new StyleSkillFiles(StylePaths.Default(Path.Combine(_root, "home"), Path.Combine(_root, "appdata")));
        _skill = new StyleSkill(files, () => _ix, () => _model, () => _settings, () => [], _hub, _time, _log);
        AddSent(30);
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

    private void AddSent(int count)
    {
        var list = new List<IndexedEmail>();
        for (int i = 0; i < count; i++)
        {
            int n = ++_seq;
            list.Add(new IndexedEmail
            {
                EntryId = "E" + n, Folder = "Sent Items", Subject = "Hi", To = $"x{n}@other.org",
                EventTime = T0.AddDays(-(1 + i)),
                Body = $"ROWW{n:00} thanks for the note, I will look at it soon.",
            });
        }
        _ix.UpsertBatch(list);
    }

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "index.db"), Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>Pair i (0 = newest): sent i+1 days ago, texts DRAFTMARK(i) / SENTMARK(i).</summary>
    private void Pairs(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var imid = $"<src{i}@x>";
            _ix.RecordAiDraftText("D" + i, "", imid, T0.AddDays(-(i + 2)), "x");
            var sent = T0.AddDays(-(i + 1));
            Assert.True(_ix.StoreEdit("D" + i, imid,
                new AiEdit("SENT" + i, sent, 0.5, 10, 10, "DRAFTMARK" + i, "SENTMARK" + i), T0));
        }
    }

    private void Unchanged(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var imid = $"<u{i}@x>";
            _ix.RecordAiDraftText("UD" + i, "", imid, T0.AddDays(-(i + 2)), "x");
            Assert.True(_ix.StoreEdit("UD" + i, imid, new AiEdit("USENT" + i, T0.AddDays(-(i + 1)), 1.0, 10, 10, null, null), T0));
        }
    }

    private List<ActivityEvent> AiEvents() => _hub.GetEvents().Where(e => e.Kind == ActivityKind.Ai).ToList();

    private async Task<StyleRunResult.Updated> Run()
    {
        var r = await _skill.RegenerateAsync(Owner, true, Ct);
        return Assert.IsType<StyleRunResult.Updated>(r);
    }

    private static int Edits(ModelRequest r) => Regex.Matches(r.UserPrompt, "<edit ").Count;

    private void AssertNoMarks()
    {
        Assert.DoesNotContain(_log.Lines, l => l.Text.Contains("DRAFTMARK") || l.Text.Contains("SENTMARK"));
        Assert.DoesNotContain(AiEvents(), e => e.Summary.Contains("DRAFTMARK") || e.Summary.Contains("SENTMARK"));
    }

    [Fact]
    public async Task Three_pairs_add_the_block_the_rules_and_the_event_text()
    {
        Pairs(3);
        var r = await Run();

        var req = Assert.Single(_model.Requests);
        Assert.Equal(StylePrompt.System + "\n\n" + StylePrompt.EditsRules, req.SystemPrompt);
        Assert.Contains("<my_edits sent_unchanged=\"0\">", req.UserPrompt);
        Assert.Equal(3, Edits(req));
        Assert.True(req.UserPrompt.IndexOf("DRAFTMARK0", StringComparison.Ordinal) < req.UserPrompt.IndexOf("DRAFTMARK1", StringComparison.Ordinal));
        Assert.True(req.UserPrompt.IndexOf("DRAFTMARK1", StringComparison.Ordinal) < req.UserPrompt.IndexOf("DRAFTMARK2", StringComparison.Ordinal));
        Assert.Equal(30, r.SampleCount);
        Assert.Equal(3, r.EditedDrafts);
        var ev = Assert.Single(AiEvents());
        Assert.Equal("writing style updated from 30 sent emails and 3 edited drafts (" + ActivityView.FormatDuration(r.Duration) + ")", ev.Summary);
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Debug && l.Text == "Writing style: 3 edited drafts, 0 sent unchanged");
        AssertNoMarks();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(5, false)]
    public async Task Prompts_are_byte_identical_without_a_block(int pairs, bool learn)
    {
        Pairs(pairs);
        _settings.LearnFromEdits = learn;
        var r = await Run();

        var req = Assert.Single(_model.Requests);
        Assert.Equal(StylePrompt.System, req.SystemPrompt);
        Assert.DoesNotContain("<my_edits", req.UserPrompt);
        Assert.EndsWith("\n</samples>", req.UserPrompt);
        Assert.Equal(0, r.EditedDrafts);
        Assert.Equal("writing style updated from 30 sent emails (" + ActivityView.FormatDuration(r.Duration) + ")", Assert.Single(AiEvents()).Summary);
    }

    [Fact]
    public async Task Sent_unchanged_counts_the_window_only()
    {
        Pairs(3);
        Unchanged(4);
        using (var c = Raw())
            c.Execute("INSERT INTO ai_edits (sent_entry_id, sent, similarity, draft_chars, sent_chars) VALUES ('OLD', @s, 1.0, 1, 1)",
                new { s = T0.AddDays(-91).ToUnixTimeSeconds() });

        await Run();

        Assert.Contains("<my_edits sent_unchanged=\"4\">", Assert.Single(_model.Requests).UserPrompt);
    }

    [Fact]
    public async Task Two_pairs_and_many_unchanged_give_no_block()
    {
        Pairs(2);
        Unchanged(10);
        var r = await Run();
        var req = Assert.Single(_model.Requests);
        Assert.DoesNotContain("<my_edits", req.UserPrompt);
        Assert.Equal(StylePrompt.System, req.SystemPrompt);
        Assert.Equal(0, r.EditedDrafts);
    }

    [Fact]
    public async Task At_most_twenty_pairs()
    {
        Pairs(22);
        var r = await Run();
        Assert.Equal(20, Edits(Assert.Single(_model.Requests)));
        Assert.Equal(20, r.EditedDrafts);
        Assert.Contains("and 20 edited drafts", Assert.Single(AiEvents()).Summary);
    }

    [Fact]
    public async Task A_failing_read_never_fails_the_run()
    {
        Pairs(3);
        using (var c = Raw()) c.Execute("DROP TABLE ai_edits");

        var r = await Run();

        var req = Assert.Single(_model.Requests);
        Assert.Equal(StylePrompt.System, req.SystemPrompt);
        Assert.DoesNotContain("<my_edits", req.UserPrompt);
        Assert.Equal(0, r.EditedDrafts);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning
            && l.Text == "Writing style: reading edited drafts failed (SqliteException)");
        AssertNoMarks();
    }

    [Fact]
    public async Task Automatic_run_uses_the_block_too()
    {
        Pairs(3);
        var r = await _skill.RunIfDueAsync(Owner, Ct);
        Assert.Equal(3, Assert.IsType<StyleRunResult.Updated>(r).EditedDrafts);
        var req = Assert.Single(_model.Requests);
        Assert.Equal(3, Edits(req));
    }
}

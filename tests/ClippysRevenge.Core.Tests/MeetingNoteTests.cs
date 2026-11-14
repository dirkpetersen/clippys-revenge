using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

/// <summary>Records ids (and "check" in the shared log); returns "" or throws Throw.</summary>
internal sealed class NoteTargets(List<string>? log = null) : IReplyTargetCheck
{
    public List<string> Ids { get; } = [];
    public Exception? Throw { get; set; }

    public Task<string> CheckAsync(string id, CancellationToken ct)
    {
        log?.Add("check");
        Ids.Add(id);
        if (Throw is not null) throw Throw;
        return Task.FromResult("");
    }
}

/// <summary>Records (plan, CanBeCanceled) (and "reply" in the shared log); returns D1 or throws Throw.</summary>
internal sealed class NoteWriter(List<string>? log = null) : IReplyDraftWriter
{
    public List<(ReplyPlan Plan, bool CanBeCanceled)> Calls { get; } = [];
    public Exception? Throw { get; set; }

    public Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
    {
        log?.Add("reply");
        Calls.Add((plan, ct.CanBeCanceled));
        if (Throw is not null) throw Throw;
        return Task.FromResult(new RepliedDraft("D1", "Re:: Sync", ""));
    }
}

public class MeetingNoteTests
{
    private static readonly DateTime S = new(2026, 11, 17, 13, 0, 0);
    private static readonly DateTime E = S.AddMinutes(90);
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7");

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
    }

    private sealed class LevelLog : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static MeetingRequest Req(string? mailbox = null) =>
        MeetingRequest.Create("bob@example.edu", "Sync", "2026-11-17T13:00", "2026-11-17T14:30", "Agenda", null, mailbox, Tz,
            new FixedTime().GetUtcNow());

    // ---- MeetingNoteText ----

    private const string Own =
        "[[Draft invite saved in your calendar: Tue 11/17 13:00–14:30 PT \"Sync\". Open it from the Clippy tray (Draft invites) or your calendar, add the Teams or Zoom link and send it. Delete this note before you send this reply.]]";

    [Fact]
    public void Text_own_calendar() => Assert.Equal(Own, MeetingNoteText.Text(S, E, "PT", "Sync", ""));

    [Fact]
    public void Text_shared_calendar() =>
        Assert.Equal(
            "[[Draft invite saved in the calendar of help@x.edu: Tue 11/17 13:00–14:30 PT \"Sync\". Open it from the Clippy tray (Draft invites) or that calendar, add the Teams or Zoom link and send it. Delete this note before you send this reply.]]",
            MeetingNoteText.Text(S, E, "PT", "Sync", "help@x.edu"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Text_no_label(string? label) =>
        Assert.Contains(": Tue 11/17 13:00–14:30 \"Sync\".", MeetingNoteText.Text(S, E, label, "Sync", ""));

    [Fact]
    public void Text_label_trimmed() =>
        Assert.Contains("13:00–14:30 CET \"Sync\"", MeetingNoteText.Text(S, E, " CET ", "Sync", ""));

    [Fact]
    public void Text_midnight() =>
        Assert.Contains("Tue 11/17 23:00–00:00 PT \"Late\"",
            MeetingNoteText.Text(new DateTime(2026, 11, 17, 23, 0, 0), new DateTime(2026, 11, 18, 0, 0, 0), "PT", "Late", ""));

    [Fact]
    public void Subject_cut()
    {
        Assert.Contains("\"" + new string('a', 80) + "\"", MeetingNoteText.Text(S, E, "PT", new string('a', 80), ""));
        foreach (var n in new[] { 81, 200 })
            Assert.Contains("\"" + new string('a', 79) + "…\"", MeetingNoteText.Text(S, E, "PT", new string('a', n), ""));
    }

    [Fact]
    public void Subject_control_chars() =>
        Assert.Contains("\"A B C D\"", MeetingNoteText.Text(S, E, "PT", "A\tB\u0007C  D\u0085", ""));

    [Fact]
    public void Html_exact()
    {
        Assert.Equal("<span style=\"background-color:#ffff00\">", DraftHtml.HighlightOpen);
        Assert.Equal("<p>" + DraftHtml.HighlightOpen + MeetingNoteText.Text(S, E, "PT", "Sync", "") + DraftHtml.HighlightClose + "</p><p>&nbsp;</p>",
            MeetingNoteText.Html(S, E, "PT", "Sync", ""));
    }

    [Fact]
    public void Html_escapes()
    {
        const string subject = "R&D <plan> \"x\" it's";
        var html = MeetingNoteText.Html(S, E, "PT", subject, "");
        Assert.Contains("\"R&amp;D &lt;plan&gt; \"x\" it's\"", html);
        Assert.DoesNotContain("&quot;", html);
        Assert.DoesNotContain("&#39;", html);
        Assert.Contains(subject, MeetingNoteText.Text(S, E, "PT", subject, ""));
    }

    [Fact]
    public void Html_cut_before_escape() =>
        Assert.Contains(new string('a', 78) + "&amp;…\"", MeetingNoteText.Html(S, E, "PT", new string('a', 78) + "&&&", ""));

    [Fact]
    public void Html_plain_text_roundtrip() =>
        Assert.Equal(MeetingNoteText.Text(S, E, "PT", "R&D <x>", ""),
            DraftHtml.ToPlainText(MeetingNoteText.Html(S, E, "PT", "R&D <x>", "")));

    [Fact]
    public void Text_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => MeetingNoteText.Text(S, E, "PT", null!, ""));
        Assert.Throws<ArgumentNullException>(() => MeetingNoteText.Text(S, E, "PT", "x", null!));
        Assert.Throws<ArgumentNullException>(() => MeetingNoteText.Html(S, E, "PT", null!, ""));
        Assert.Throws<ArgumentNullException>(() => MeetingNoteText.Html(S, E, "PT", "x", null!));
    }

    [Fact]
    public void Error_texts()
    {
        const string head = "The meeting was saved, but the note in Drafts couldn't be created: ";
        Assert.Equal(head + "boom.", MeetingNoteText.Error("boom"));
        Assert.Equal(head + "Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.",
            MeetingNoteText.Error(OutlookUnavailableException.DefaultMessage));
        Assert.EndsWith(": x.", MeetingNoteText.Error("x..."));
        foreach (var r in new string?[] { null, "  ", "." })
            Assert.EndsWith(": an unexpected error.", MeetingNoteText.Error(r));
        Assert.Equal(head.Length + 400 + 1, MeetingNoteText.Error(new string('r', 1000)).Length);
    }

    [Fact]
    public void Reason_cases()
    {
        Assert.Equal("m", MeetingNoteText.Reason(new McpException("m")));
        var nf = new ItemNotFoundException("abc");
        Assert.Equal(nf.Message, MeetingNoteText.Reason(nf));
        Assert.Equal(OutlookUnavailableException.DefaultMessage, MeetingNoteText.Reason(new OutlookUnavailableException()));
        var other = MeetingNoteText.Reason(new InvalidOperationException("SECRET"));
        Assert.Equal("an unexpected error (InvalidOperationException)", other);
        Assert.DoesNotContain("SECRET", other);
        Assert.Equal("an unexpected error", MeetingNoteText.Reason(null));
    }

    [Fact]
    public void SavedSuffix_exact() =>
        Assert.Equal(" A note was saved in Drafts as a reply to the email.", MeetingNoteText.SavedSuffix);

    // ---- MeetingNotes ----

    private sealed class Rig
    {
        public NoteTargets Targets { get; } = new();
        public NoteWriter Writer { get; } = new();
        public LevelLog Log { get; } = new();
        public string? Sig { get; set; } = "<p>Sig</p>";
        public Func<string?> SigFunc { get; set; }
        public MeetingNotes Notes { get; }

        public Rig()
        {
            SigFunc = () => Sig;
            Notes = new MeetingNotes(Targets, Writer, () => SigFunc(), Log);
        }
    }

    [Fact]
    public void Ctor_nulls()
    {
        var t = new NoteTargets();
        var w = new NoteWriter();
        Assert.Throws<ArgumentNullException>(() => new MeetingNotes(null!, w, () => null));
        Assert.Throws<ArgumentNullException>(() => new MeetingNotes(t, null!, () => null));
        Assert.Throws<ArgumentNullException>(() => new MeetingNotes(t, w, null!));
        Assert.NotNull(new MeetingNotes(t, w, () => null, null));
    }

    [Fact]
    public async Task Check_trims_and_delegates()
    {
        var r = new Rig();
        await r.Notes.CheckAsync("  ID1 ", TestContext.Current.CancellationToken);
        Assert.Equal(["ID1"], r.Targets.Ids);
    }

    [Fact]
    public async Task Check_propagates()
    {
        var r = new Rig();
        var thrown = new ItemNotFoundException("ID9");
        r.Targets.Throw = thrown;
        var ex = await Assert.ThrowsAsync<ItemNotFoundException>(() => r.Notes.CheckAsync("ID9", TestContext.Current.CancellationToken));
        Assert.Same(thrown, ex);
    }

    [Fact]
    public void Plan_shape()
    {
        var r = new Rig();
        var req = Req();
        var p = r.Notes.Plan(" ID1 ", req, "PT");
        Assert.Equal("ID1", p.Id);
        Assert.Null(p.Mailbox);
        Assert.False(p.ReplyAll);
        Assert.Equal("tool", p.Marker);
        Assert.Equal(WorkPriority.Interactive, p.Priority);
        Assert.Equal(MeetingNoteText.Html(req.Start, req.End, "PT", "Sync", "") + "<br><p>Sig</p>", p.HtmlFragment);
        Assert.True(p.SignatureAdded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Plan_without_signature(string? sig)
    {
        var r = new Rig { Sig = sig };
        var req = Req();
        var p = r.Notes.Plan("ID1", req, "PT");
        Assert.Equal(MeetingNoteText.Html(req.Start, req.End, "PT", "Sync", ""), p.HtmlFragment);
        Assert.False(p.SignatureAdded);
    }

    [Fact]
    public void Plan_shared_meeting_still_replies_in_the_emails_mailbox()
    {
        var p = new Rig().Notes.Plan("ID1", Req("help@x.edu"), "PT");
        Assert.Null(p.Mailbox);
        Assert.Contains("the calendar of help@x.edu", p.HtmlFragment);
        Assert.Contains("or that calendar", p.HtmlFragment);
    }

    [Fact]
    public async Task Save_success()
    {
        var r = new Rig();
        var req = Req();
        var o = await r.Notes.SaveAsync("ID1", req, "PT", TestContext.Current.CancellationToken);
        Assert.Equal(new MeetingNoteOutcome("D1", null), o);
        var (plan, _) = Assert.Single(r.Writer.Calls);
        Assert.Equal(r.Notes.Plan("ID1", req, "PT"), plan);
        Assert.Empty(r.Log.Entries);
    }

    [Fact]
    public async Task Save_mcp_failure()
    {
        var r = new Rig { };
        r.Writer.Throw = new McpException("Outlook could not create the reply draft: x");
        var o = await r.Notes.SaveAsync("ID1", Req(), "PT", TestContext.Current.CancellationToken);
        Assert.Null(o.DraftId);
        Assert.Equal("The meeting was saved, but the note in Drafts couldn't be created: Outlook could not create the reply draft: x.", o.Error);
        var (level, msg) = Assert.Single(r.Log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("Meeting draft: the note in Drafts couldn't be created (McpException).", msg);
    }

    [Fact]
    public async Task Save_unavailable()
    {
        var r = new Rig();
        r.Writer.Throw = new OutlookUnavailableException();
        var o = await r.Notes.SaveAsync("ID1", Req(), "PT", TestContext.Current.CancellationToken);
        Assert.EndsWith("Ask the user to start Outlook (tray → Start Outlook) and try again.", o.Error);
    }

    [Fact]
    public async Task Save_unexpected_failure()
    {
        var r = new Rig();
        r.Writer.Throw = new InvalidOperationException("SECRET");
        var o = await r.Notes.SaveAsync("ID1", Req(), "PT", TestContext.Current.CancellationToken);
        Assert.Equal("The meeting was saved, but the note in Drafts couldn't be created: an unexpected error (InvalidOperationException).", o.Error);
        foreach (var s in r.Log.Entries.Select(e => e.Message).Append(o.Error!))
        {
            Assert.DoesNotContain("SECRET", s);
            Assert.DoesNotContain("Sync", s.Replace("Meeting draft", ""));
            Assert.DoesNotContain("ID1", s);
        }
    }

    [Fact]
    public async Task Save_cancelled_inside_writer()
    {
        var r = new Rig();
        r.Writer.Throw = new OperationCanceledException();
        var o = await r.Notes.SaveAsync("ID1", Req(), "PT", TestContext.Current.CancellationToken);
        Assert.Null(o.DraftId);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public async Task Save_signature_throws()
    {
        var r = new Rig { SigFunc = () => throw new IOException("disk") };
        var o = await r.Notes.SaveAsync("ID1", Req(), "PT", TestContext.Current.CancellationToken);
        Assert.Equal("The meeting was saved, but the note in Drafts couldn't be created: an unexpected error (IOException).", o.Error);
        Assert.Empty(r.Writer.Calls);
    }

    [Fact]
    public async Task Save_null_args()
    {
        var r = new Rig();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(() => r.Notes.SaveAsync(null!, Req(), "PT", ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => r.Notes.SaveAsync("ID1", null!, "PT", ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => r.Notes.SaveAsync("ID1", Req(), null!, ct));
    }
}

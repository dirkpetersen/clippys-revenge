using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerAttachmentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string M = "FIXTURE-SECRET-4711";
    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;
    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private static AiAttachmentRead.File Txt(string s) => new(Encoding.UTF8.GetBytes(s));

    private static Task<DraftPassResult> Pass(DraftHarness h) => h.Worker.RunPassAsync(Ct);

    private static AttachmentInfo[] Notes1 => [new AttachmentInfo(1, "notes.txt", 40, false)];

    /// <summary>X1 in the own Inbox with notes.txt (text "budget " + M), consent and flags as the brief says.</summary>
    private static DraftHarness Fx(out FakeAttachmentSource src, IReadOnlyList<AttachmentInfo>? atts = null,
        string body = "BODYTEXT-123", string sender = "ann@contoso.example", bool enqueue = true)
    {
        src = new FakeAttachmentSource();
        var h = new DraftHarness(attachments: src);
        h.Ai.ReadAttachments = true;
        h.Ai.AttachmentsNoticeShown = true;
        h.Seed("X1", attachments: atts ?? Notes1, body: body, sender: sender);
        src.Returns("X1", 1, Txt("budget " + M));
        if (enqueue) h.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>"));
        return h;
    }

    /// <summary>The same seed in a harness without any attachment source (M18 behavior).</summary>
    private static DraftHarness Base(IReadOnlyList<AttachmentInfo>? atts = null, string body = "BODYTEXT-123")
    {
        var h = new DraftHarness();
        h.Seed("X1", attachments: atts ?? Notes1, body: body);
        h.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>"));
        return h;
    }

    private static string DraftEvent(DraftHarness h) => Assert.Single(h.Events(ActivityKind.Draft)).Summary;

    private static string[] AttLines(DraftHarness h) =>
        h.Log.Lines.Where(l => l.StartsWith("AI drafts: attachments ", StringComparison.Ordinal)).ToArray();

    // ---------------------------------------------------------------- gate and byte-identity

    [Fact]
    public async Task Gate_closed_by_the_setting_is_byte_identical_to_no_source()
    {
        using var h = Fx(out var src);
        h.Ai.ReadAttachments = false;
        using var b = Base();

        await Pass(h);
        await Pass(b);

        Assert.Equal(0, src.RootsCalls);
        Assert.Equal(0, src.ReadCalls);
        Assert.Equal(Assert.Single(b.Model.Requests), Assert.Single(h.Model.Requests));
        Assert.Equal(Assert.Single(b.Writer.Plans).HtmlFragment, Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.DoesNotContain(" (attachments:", DraftEvent(h), StringComparison.Ordinal);
        Assert.Empty(AttLines(h));
    }

    [Fact]
    public async Task No_source_is_byte_identical_and_has_no_notice_or_log_line()
    {
        using var h = Base();
        h.Ai.ReadAttachments = true;
        h.Ai.AttachmentsNoticeShown = false;
        using var other = Fx(out _);
        other.Ai.ReadAttachments = false;

        await Pass(h);
        await Pass(other);

        Assert.Equal(Assert.Single(other.Model.Requests), Assert.Single(h.Model.Requests));
        Assert.Empty(h.NoticeMarks);
        Assert.DoesNotContain(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
        Assert.Empty(AttLines(h));
    }

    [Fact]
    public async Task Email_without_attachments_or_links_still_runs_the_collection_once()
    {
        using var h = Fx(out var src, atts: []);
        using var b = Base(atts: []);

        await Pass(h);
        await Pass(b);

        Assert.Equal(1, src.RootsCalls);
        Assert.Equal(0, src.ReadCalls);
        Assert.Equal(Assert.Single(b.Model.Requests), Assert.Single(h.Model.Requests));
        Assert.DoesNotContain(" (attachments:", DraftEvent(h), StringComparison.Ordinal);
        var line = Assert.Single(AttLines(h));
        Assert.Matches(@"^AI drafts: attachments 0 read, 0 truncated, 0 skipped, 0 links, 0 chars in \d+ ms$", line);
    }

    // ---------------------------------------------------------------- automatic path

    [Fact]
    public async Task Automatic_pass_adds_the_attachment_block_and_the_event_suffix()
    {
        using var h = Fx(out var src);

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Completed, r.End);
        Assert.Equal([("", "X1", WorkPriority.Background)], src.Calls);
        var req = Assert.Single(h.Model.Requests);
        Assert.Contains("<attachments>", req.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("name=\"notes.txt\" source=\"email\" kind=\"file\" status=\"read\"", req.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(M, req.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(DraftPrompt.AttachmentsNote, req.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(ModelCallKind.Draft, req.Kind);
        Assert.Equal("AI reply draft created: Re:: Budget (attachments: 1 read, 0 not read)", DraftEvent(h));
        Assert.DoesNotContain(M, Assert.Single(h.Writer.Plans).HtmlFragment, StringComparison.Ordinal);
    }

    private static DraftHarness TwoFiles(out FakeAttachmentSource src, bool reasonLine)
    {
        var h = Fx(out src, atts: [new AttachmentInfo(1, "a.pdf", 10, false), new AttachmentInfo(2, "old.doc", 20, false)], enqueue: false);
        h.Ai.ReasonLine = reasonLine;
        src.Returns("X1", 1, Txt("x"));
        h.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>", note: "Ann asked"));
        return h;
    }

    private static readonly AttachmentNote?[] TwoNotes =
    [
        new AttachmentNote(AttachmentNoteKind.Damaged, "a.pdf", false),
        new AttachmentNote(AttachmentNoteKind.Format, "old.doc", false, "doc"),
    ];

    [Fact]
    public async Task Notes_follow_the_reason_line_and_come_before_the_body()
    {
        using var h = TwoFiles(out var src, reasonLine: true);

        await Pass(h);

        var fragment = Assert.Single(h.Writer.Plans).HtmlFragment;
        var top = DraftReasonLine.Html("Ann asked") + AttachmentNotes.Html(TwoNotes);
        Assert.StartsWith(top, fragment, StringComparison.Ordinal);
        Assert.True(fragment.IndexOf("Thanks", StringComparison.Ordinal) > top.Length - 1);
        Assert.EndsWith("(attachments: 0 read, 2 not read)", DraftEvent(h), StringComparison.Ordinal);
        Assert.Equal(1, src.ReadCalls);
    }

    [Fact]
    public async Task Notes_appear_without_the_reason_line()
    {
        using var h = TwoFiles(out _, reasonLine: false);

        await Pass(h);

        Assert.StartsWith(AttachmentNotes.Html(TwoNotes), Assert.Single(h.Writer.Plans).HtmlFragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_mailbox_candidate_passes_its_key_to_the_source()
    {
        var src = new FakeAttachmentSource();
        using var h = new DraftHarness(attachments: src);
        h.Ai.ReadAttachments = true;
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("X1", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>", attachments: Notes1);
        src.Returns("X1", 1, Txt("budget " + M));
        h.Model.Enqueue(OkReply(0.9));

        await Pass(h);

        Assert.Equal("helpdesk@example.org", Assert.Single(src.Calls).Mailbox);
    }

    [Fact]
    public async Task Thread_attachments_are_read_after_the_anchor_and_named_by_origin()
    {
        using var h = Fx(out var src, body: "see plan.txt");
        h.Seed("T1", at: Now - TimeSpan.FromMinutes(70), attachments: [new AttachmentInfo(1, "plan.txt", 30, false)],
            sender: "carl@contoso.example", body: "T1-BODY");
        src.Returns("T1", 1, Txt("plan " + M));

        await Pass(h);

        Assert.Equal(["X1", "T1"], src.Calls.Select(c => c.EntryId).ToArray());
        var prompt = Assert.Single(h.Model.Requests).UserPrompt;
        var m = Regex.Match(prompt, "<attachment [^>]*name=\"plan.txt\" source=\"thread\"[^>]*>");
        Assert.True(m.Success, prompt);
        Assert.Contains("from=", m.Value, StringComparison.Ordinal);
        Assert.Contains("date=", m.Value, StringComparison.Ordinal);
    }

    private const string Link = "https://contoso.sharepoint.com/sites/x/Shared%20Documents/f.txt";

    private static DraftHarness LinkFx(out FakeAttachmentSource src, string sender, out string mount)
    {
        var h = Fx(out src, atts: [], body: "see " + Link, sender: sender);
        mount = Path.Combine(h.Dir, "mount");
        Directory.CreateDirectory(mount);
        File.WriteAllText(Path.Combine(mount, "f.txt"), "linked " + M);
        src.Roots = [new SyncedRoot("https://contoso.sharepoint.com/sites/x/Shared Documents/", mount, "teamsite", false)];
        h.Mailbox.Identity = new AiIdentity("Me", "");
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        return h;
    }

    [Fact]
    public async Task Sender_rule_uses_the_resolved_own_address()
    {
        using var h = LinkFx(out _, "boss@example.org", out _);

        await Pass(h);

        var prompt = Assert.Single(h.Model.Requests).UserPrompt;
        Assert.Contains("kind=\"link\"", prompt, StringComparison.Ordinal);
        Assert.Contains("status=\"read\"", prompt, StringComparison.Ordinal);
        Assert.Contains("linked " + M, prompt, StringComparison.Ordinal);

        using var h2 = LinkFx(out _, "x@other.example", out _);
        await Pass(h2);
        var p2 = Assert.Single(h2.Model.Requests).UserPrompt;
        Assert.Contains("reason=\"from outside your organization\"", p2, StringComparison.Ordinal);
        Assert.DoesNotContain("linked " + M, p2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_budget_skips_the_rest_but_still_drafts()
    {
        using var h = Fx(out var src, atts:
        [
            new AttachmentInfo(1, "a.txt", 10, false), new AttachmentInfo(2, "b.txt", 10, false),
            new AttachmentInfo(3, "c.txt", 10, false),
        ]);
        src.Returns("X1", 1, Txt("one")).Returns("X1", 2, Txt("two")).Returns("X1", 3, Txt("three"));
        var first = true;
        src.OnRead = () =>
        {
            if (!first) return;
            first = false;
            h.Time.Advance(TimeSpan.FromSeconds(21));
        };

        await Pass(h);

        Assert.Equal(1, src.ReadCalls);
        var prompt = Assert.Single(h.Model.Requests).UserPrompt;
        Assert.Equal(2, Regex.Matches(prompt, "reason=\"ran out of time for attachments\"").Count);
        Assert.Single(Regex.Matches(prompt, "status=\"read\""));
        var fragment = Assert.Single(h.Writer.Plans).HtmlFragment;
        Assert.Contains("Couldn't read b.txt (it took too long).", fragment, StringComparison.Ordinal);
        Assert.Contains("Couldn't read c.txt (it took too long).", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outlook_gone_while_reading_still_calls_the_model()
    {
        using var h = Fx(out var src);
        src.Throws("X1", 1, new OutlookUnavailableException());

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Completed, r.End);
        var prompt = Assert.Single(h.Model.Requests).UserPrompt;
        Assert.Contains("reason=\"Outlook isn't running\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_failures_do_not_block_the_draft_and_never_log_details()
    {
        using var h = Fx(out var src);
        src.Throws("X1", 1, new InvalidOperationException("C:\\secret\\" + M));

        await Pass(h);

        Assert.Single(h.Model.Requests);
        Assert.Single(h.Writer.Plans);
        Assert.Contains("reason=\"the file seems damaged\"", h.Model.Requests[0].UserPrompt, StringComparison.Ordinal);
        Assert.Contains(h.Log.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains(M, StringComparison.Ordinal) || l.Contains("secret", StringComparison.Ordinal));

        using var h2 = Fx(out var src2);
        src2.RootsError = new IOException("x");
        await Pass(h2);
        Assert.Single(h2.Model.Requests);
        Assert.Single(h2.Writer.Plans);
        Assert.Contains(M, h2.Model.Requests[0].UserPrompt, StringComparison.Ordinal);
        Assert.Contains(h2.Log.Lines, l => l.Contains("IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(h2.Log.Lines, l => l.Contains(M, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collection_runs_inside_the_drafting_scope()
    {
        using var h = Fx(out var src);
        ActivityState? seen = null;
        src.OnRead = () => seen = h.Hub.State;

        await Pass(h);

        Assert.Equal(ActivityState.Drafting, seen);
    }

    [Fact]
    public async Task Cancellation_while_reading_ends_the_pass_without_a_call_or_row()
    {
        using var h = Fx(out var src);
        using var cts = new CancellationTokenSource();
        src.OnRead = cts.Cancel;

        var r = await h.Worker.RunPassAsync(cts.Token);

        Assert.Equal(DraftPassEnd.Cancelled, r.End);
        Assert.Empty(h.Model.Requests);
        Assert.Null(h.Decision("X1"));
    }

    [Fact]
    public async Task Daily_limit_is_checked_before_any_attachment_work()
    {
        using var h = Fx(out var src);
        h.Ai.DailyCap = 1;
        h.Indexes.For("").UpsertDecision(new AiDecision("Z", null, null, Now.AddHours(-1), AiOutcome.NoReply, ModelCalls: 1));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.DailyLimit, r.End);
        Assert.Equal(0, src.RootsCalls);
        Assert.Equal(0, src.ReadCalls);
    }

    // ---------------------------------------------------------------- deleted items

    [Fact]
    public async Task Rows_in_deleted_items_are_never_a_source()
    {
        using var h = Fx(out var src, body: "see plan.txt and old.txt and kept.txt");
        var plan = new[] { new AttachmentInfo(1, "plan.txt", 30, false) };
        h.Seed("T1", at: Now - TimeSpan.FromMinutes(70), attachments: plan, folder: "Deleted Items", body: "T1");
        h.Seed("T2", at: Now - TimeSpan.FromMinutes(75), attachments: [new AttachmentInfo(1, "old.txt", 30, false)],
            folder: "deleted items\\Old", body: "T2");
        h.Seed("T3", at: Now - TimeSpan.FromMinutes(80), attachments: [new AttachmentInfo(1, "kept.txt", 30, false)],
            folder: "Drafts", body: "T3");
        src.Returns("T1", 1, Txt("a")).Returns("T2", 1, Txt("b")).Returns("T3", 1, Txt("c"));

        await Pass(h);

        Assert.Equal(["X1", "T3"], src.Calls.Select(c => c.EntryId).ToArray());
    }

    // ---------------------------------------------------------------- rescan

    private static DraftHarness RescanFx(out FakeAttachmentSource src)
    {
        var h = Fx(out src, enqueue: false);
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Ai.MeetingTimes = false;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Budget", "", FP2);
        h.Seed("X1", at: Now - Hours(2), attachments: Notes1);
        h.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>"));
        return h;
    }

    [Fact]
    public async Task Rescan_collects_at_interactive_priority_with_the_suffix()
    {
        using var h = RescanFx(out var src);

        await h.Worker.RescanAsync(Ct);

        Assert.Equal(WorkPriority.Interactive, src.Calls[0].Priority);
        var req = Assert.Single(h.Model.Requests);
        Assert.Contains("<attachments>", req.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(ModelCallKind.Rescan, req.Kind);
        Assert.Equal("AI reply draft created: Re:: Budget (attachments: 1 read, 0 not read)", DraftEvent(h));
    }

    [Fact]
    public async Task Cancel_rescan_stops_a_running_collection()
    {
        using var h = RescanFx(out var src);
        src.OnRead = () => h.Worker.CancelRescan();

        var r = await h.Worker.RescanAsync(Ct);

        Assert.Equal(RescanStop.Cancelled, r.Stop);
        Assert.Empty(h.Model.Requests);
        Assert.Null(h.Decision("X1"));
    }

    // ---------------------------------------------------------------- manual

    private static DraftHarness ManualFx(out FakeAttachmentSource src, bool read = true)
    {
        src = new FakeAttachmentSource();
        var h = new DraftHarness(attachments: src);
        h.Ai.Enabled = false;
        h.Ai.ReadAttachments = read;
        h.Seed("L1", attachments: []);
        h.Mailbox.Selected = new SelectedEmail("L1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "L1",
            Folder = "Inbox",
            Subject = "Budget",
            SenderName = "Ann",
            SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>",
            ConversationTopic = "Budget",
            Body = "see live.txt",
            EventTime = Now.AddMinutes(-5),
            Attachments = [new AttachmentInfo(1, "live.txt", 40, false)],
        });
        src.Returns("L1", 1, Txt("live " + M));
        h.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>"));
        return h;
    }

    [Fact]
    public async Task Manual_path_uses_the_live_attachment_list_at_interactive_priority()
    {
        using var h = ManualFx(out var src);

        var r = await h.Worker.DraftSelectedAsync(Ct);

        Assert.Equal(new ManualDraftResult(true, DraftTexts.ManualDone), r);
        Assert.Equal([("", "L1", WorkPriority.Interactive)], src.Calls);
        var req = Assert.Single(h.Model.Requests);
        Assert.Contains("name=\"live.txt\"", req.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(ModelCallKind.Manual, req.Kind);
        Assert.EndsWith("(attachments: 1 read, 0 not read)", DraftEvent(h), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_path_with_the_setting_off_is_byte_identical_to_no_source()
    {
        using var h = ManualFx(out var src, read: false);
        using var b = new DraftHarness();
        b.Ai.Enabled = false;
        b.Seed("L1", attachments: []);
        b.Mailbox.Selected = h.Mailbox.Selected;
        b.Mailbox.Live = h.Mailbox.Live;
        b.Model.Enqueue(OkReply(0.9, body: "<p>Thanks</p>"));

        await h.Worker.DraftSelectedAsync(Ct);
        await b.Worker.DraftSelectedAsync(Ct);

        Assert.Empty(src.Calls);
        Assert.Equal(Assert.Single(b.Model.Requests), Assert.Single(h.Model.Requests));
    }

    // ---------------------------------------------------------------- revisit

    private static DraftHarness RevisitFx(out FakeAttachmentSource src)
    {
        src = new FakeAttachmentSource();
        var h = new DraftHarness(attachments: src);
        h.Ai.ReadAttachments = true;
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = false;
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Seed("S1", at: Now - Hours(3), topic: "Ski", subject: "Ski trip", imid: "<s1@x>", attachments: []);
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - Hours(3), AiOutcome.Drafted, null, 1, 0, 0.9,
            "D0", Now - Hours(3) + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - Hours(3) + TimeSpan.FromMinutes(5), FP, Now - Hours(3));
        h.Seed("D0", at: Now - Hours(2), topic: "Ski", subject: "Re:: Ski trip", folder: "Drafts",
            sender: "me@example.org", attachments: [new AttachmentInfo(1, "draft.txt", 5, false)]);
        h.Seed("N1", at: Now - TimeSpan.FromMinutes(30), topic: "Ski", subject: "Re: Ski trip", sender: "bob@contoso.example",
            to: "Ann <ann@contoso.example>", body: "N1-BODY", attachments: [new AttachmentInfo(1, "n.txt", 10, false)]);
        src.Returns("N1", 1, Txt("new " + M)).Returns("D0", 1, Txt("draft " + M));
        h.Mailbox.DraftInfo = (id, _) => id == "D0" ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        return h;
    }

    private static ModelResult R(string action, double conf, string? body = null)
    {
        var d = new Dictionary<string, object?> { ["action"] = action, ["confidence"] = conf, ["reason"] = "SECRET-REASON" };
        if (body is not null) d["body_html"] = body;
        return Ok(JsonSerializer.Serialize(d));
    }

    [Fact]
    public async Task Revisit_replace_collects_for_the_reply_target_and_excludes_the_old_draft()
    {
        using var h = RevisitFx(out var src);
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Contains(src.Calls, c => c.EntryId == "N1");
        Assert.DoesNotContain(src.Calls, c => c.EntryId == "D0");
        Assert.All(src.Calls, c => Assert.Equal(WorkPriority.Background, c.Priority));
        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(ModelCallKind.Revisit, req.Kind);
        var a = req.UserPrompt.IndexOf("<attachments>", StringComparison.Ordinal);
        var d = req.UserPrompt.IndexOf("<current_draft ", StringComparison.Ordinal);
        Assert.True(a >= 0 && d > a, req.UserPrompt);
        Assert.Contains(DraftPrompt.AttachmentsNote, req.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "Re:: Ski", " (attachments: 1 read, 0 not read)"),
            DraftEvent(h));
        Assert.DoesNotContain(M, Assert.Single(h.Writer.Plans).HtmlFragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revisit_keep_collects_once_and_has_no_event()
    {
        using var h = RevisitFx(out var src);
        h.Model.Enqueue(R("keep", 0.9));

        await Pass(h);

        Assert.Single(src.Calls);
        Assert.Equal("N1", src.Calls[0].EntryId);
        Assert.Empty(h.Events(ActivityKind.Draft));
    }

    [Fact]
    public async Task Revisit_inside_a_rescan_collects_at_interactive_priority()
    {
        using var h = RevisitFx(out var src);
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await h.Worker.RescanAsync(Ct);

        Assert.NotEmpty(src.Calls);
        Assert.All(src.Calls, c => Assert.Equal(WorkPriority.Interactive, c.Priority));
        Assert.Equal(ModelCallKind.Revisit, Assert.Single(h.Model.Requests).Kind);
    }

    // ---------------------------------------------------------------- notice

    [Fact]
    public async Task Notice_is_shown_once_per_process()
    {
        using var h = Fx(out var src);
        h.Ai.AttachmentsNoticeShown = false;
        h.Seed("X2", age: TimeSpan.FromMinutes(9), attachments: [new AttachmentInfo(1, "second.txt", 40, false)]);
        src.Returns("X2", 1, Txt("two"));
        h.Model.Enqueue(OkReply(0.9));

        await Pass(h);

        Assert.Equal([AiNotice.Attachments], h.NoticeMarks);
        Assert.Single(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
        Assert.Single(h.Shown, b => b.Text == DraftAttachmentTexts.Notice && b.Kind == BalloonKind.Info);

        h.Seed("X3", age: TimeSpan.FromMinutes(8), attachments: [new AttachmentInfo(1, "third.txt", 40, false)]);
        src.Returns("X3", 1, Txt("three"));
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);

        Assert.Equal([AiNotice.Attachments], h.NoticeMarks);
        Assert.Single(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
        Assert.Single(h.Shown, b => b.Text == DraftAttachmentTexts.Notice);
    }

    [Fact]
    public async Task Notice_is_not_shown_when_off_without_a_source_or_already_shown()
    {
        using var off = Fx(out _);
        off.Ai.AttachmentsNoticeShown = false;
        off.Ai.ReadAttachments = false;
        await Pass(off);

        using var none = Base();
        none.Ai.AttachmentsNoticeShown = false;
        await Pass(none);

        using var shown = Fx(out _);
        await Pass(shown);

        foreach (var h in new[] { off, none, shown })
        {
            Assert.Empty(h.NoticeMarks);
            Assert.DoesNotContain(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
            Assert.DoesNotContain(h.Shown, b => b.Text == DraftAttachmentTexts.Notice);
        }
    }

    [Fact]
    public async Task Notice_survives_a_failing_save_and_logs_only_the_type()
    {
        using var h = Fx(out _);
        h.Ai.AttachmentsNoticeShown = false;
        h.MarkNoticeShown = _ => throw new IOException("C:\\secret\\" + M);

        await Pass(h);

        Assert.Single(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
        Assert.Single(h.Shown, b => b.Text == DraftAttachmentTexts.Notice);
        var warn = Assert.Single(h.Log.Lines, l => l.Contains("attachments notice", StringComparison.Ordinal));
        Assert.Contains("IOException", warn, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("secret", StringComparison.Ordinal));
        Assert.Single(h.Writer.Plans);
    }

    [Fact]
    public async Task Notice_also_fires_before_the_first_manual_collection()
    {
        using var h = ManualFx(out _);
        h.Ai.AttachmentsNoticeShown = false;

        await h.Worker.DraftSelectedAsync(Ct);

        Assert.Equal([AiNotice.Attachments], h.NoticeMarks);
        Assert.Single(h.Events(ActivityKind.Ai), e => e.Summary == DraftAttachmentTexts.Notice);
    }

    // ---------------------------------------------------------------- privacy

    private static readonly string[] Forbidden =
    [
        M, "notes.txt", "plan.txt", "live.txt", "n.txt", "f.txt", "contoso.sharepoint.com", "the file seems damaged",
        "from outside your organization",
    ];

    private static void AssertClean(DraftHarness h, string? mount = null)
    {
        var texts = h.AllText().Concat(h.Shown.Select(b => b.Text)).ToList();
        foreach (var f in Forbidden.Concat(mount is null ? [] : [mount]))
            Assert.DoesNotContain(texts, t => t.Contains(f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_events_and_balloons_hold_no_file_names_urls_paths_or_text()
    {
        using (var h = Fx(out _)) { h.Ai.AttachmentsNoticeShown = false; await Pass(h); AssertClean(h); }

        using (var h = Fx(out var src, body: "see plan.txt"))
        {
            h.Seed("T1", at: Now - TimeSpan.FromMinutes(70), attachments: [new AttachmentInfo(1, "plan.txt", 30, false)]);
            src.Returns("T1", 1, Txt("plan " + M));
            await Pass(h);
            AssertClean(h);
        }

        using (var h = LinkFx(out _, "x@other.example", out var mount)) { await Pass(h); AssertClean(h, mount); }
        using (var h = LinkFx(out _, "boss@example.org", out var mount)) { await Pass(h); AssertClean(h, mount); }

        using (var h = ManualFx(out _)) { await h.Worker.DraftSelectedAsync(Ct); AssertClean(h); }

        using (var h = RevisitFx(out _))
        {
            h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));
            await Pass(h);
            AssertClean(h);
        }
    }

    [Fact]
    public async Task Extracted_text_never_reaches_the_index_files()
    {
        byte[] utf8, utf16;
        using var h = Fx(out _);
        await Pass(h);
        Assert.Contains(M, Assert.Single(h.Model.Requests).UserPrompt, StringComparison.Ordinal);   // positive control
        SqliteConnection.ClearAllPools();
        utf8 = Encoding.UTF8.GetBytes(M);
        utf16 = Encoding.Unicode.GetBytes(M);

        var files = Directory.GetFiles(h.Dir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, Ct);
            var bytes = ms.ToArray();
            Assert.False(bytes.AsSpan().IndexOf(utf8) >= 0, f);
            Assert.False(bytes.AsSpan().IndexOf(utf16) >= 0, f);
        }
    }

    [Fact]
    public async Task Learning_from_edits_never_lets_an_echoed_attachment_text_reach_the_index_files()
    {
        using var h = Fx(out _, enqueue: false);
        h.Ai.LearnFromEdits = true;
        h.Model.Enqueue(OkReply(0.9, body: "<p>echo " + M + "</p>"));
        await Pass(h);
        Assert.Contains(M, Assert.Single(h.Writer.Plans).HtmlFragment, StringComparison.Ordinal);   // positive control
        Assert.Null(h.Indexes.For("").GetAiDraftText("D1"));
        SqliteConnection.ClearAllPools();
        var utf8 = Encoding.UTF8.GetBytes(M);
        var utf16 = Encoding.Unicode.GetBytes(M);

        var files = Directory.GetFiles(h.Dir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, Ct);
            var bytes = ms.ToArray();
            Assert.False(bytes.AsSpan().IndexOf(utf8) >= 0, f);
            Assert.False(bytes.AsSpan().IndexOf(utf16) >= 0, f);
        }
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = MailboxIndexes.PathFor(h.Dir, ""), Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ai_draft_texts";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Debug_count_line_is_exactly_one_per_prompt_with_counts_only()
    {
        using var h = Fx(out _);
        await Pass(h);

        var line = Assert.Single(AttLines(h));
        Assert.Matches(@"^AI drafts: attachments \d+ read, \d+ truncated, \d+ skipped, \d+ links, \d+ chars in \d+ ms$", line);
    }

    // ---------------------------------------------------------------- counts and suffix

    [Fact]
    public void Counts_flags_and_event_suffix()
    {
        var skipped = new AttachmentCounts(0, 0, 3, 0, 0, 0);
        Assert.False(skipped.UsedText);
        Assert.True(skipped.Considered);
        Assert.True(new AttachmentCounts(1, 0, 0, 0, 0, 0).UsedText);
        Assert.True(new AttachmentCounts(0, 1, 0, 0, 0, 0).UsedText);
        Assert.False(AttachmentCounts.None.UsedText);
        Assert.False(AttachmentCounts.None.Considered);
        Assert.Equal("", DraftAttachmentTexts.EventSuffix(AttachmentCounts.None));
        Assert.Equal("", DraftAttachmentTexts.EventSuffix(null));
        Assert.Equal(" (attachments: 3 read, 4 not read)", DraftAttachmentTexts.EventSuffix(new AttachmentCounts(2, 1, 4, 0, 0, 0)));
    }
}

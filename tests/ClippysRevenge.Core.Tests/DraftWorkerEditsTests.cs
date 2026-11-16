using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerEditsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BodyWord = "BODYWORD";
    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;
    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private const string ModelBody = "<p>Hi Ann,</p><p>Call +1 503 555 0100 about " + BodyWord + ".</p>";

    private static Task<DraftPassResult> Pass(DraftHarness h) => h.Worker.RunPassAsync(Ct);

    private static DraftHarness Fx()
    {
        var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Seed("X1");
        return h;
    }

    private static long Rows(DraftHarness h, string mailbox = "")
    {
        h.Indexes.For(mailbox);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = MailboxIndexes.PathFor(h.Dir, mailbox), Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ai_draft_texts";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string Expected(string body) => EditTexts.Prepare(DraftHtml.ToPlainText(DraftHtml.Sanitize(body)), null);

    [Fact]
    public async Task Automatic_records_the_model_body_only()
    {
        using var h = Fx();
        h.Ai.ReasonLine = true;
        h.SignatureHtml = "<p>Peter SIG</p>";
        h.Model.Enqueue(OkReply(0.9, body: ModelBody, note: "Clippy reason about the budget."));

        await Pass(h);

        var row = h.Indexes.For("").GetAiDraftText("D1")!;
        Assert.Equal("", row.Mailbox);
        Assert.Equal("<X1@contoso.example>", row.SourceImid);
        Assert.Equal(Expected(ModelBody), row.Text);
        Assert.Contains("<phone>", row.Text, StringComparison.Ordinal);
        Assert.Contains(BodyWord, row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Clippy", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Peter SIG", row.Text, StringComparison.Ordinal);
        Assert.Equal(AiDraftTextState.Pending, row.State);
        Assert.Equal(Now, row.Created);

        var fragment = Assert.Single(h.Writer.Plans).HtmlFragment;
        Assert.Contains("Clippy", fragment, StringComparison.Ordinal);
        Assert.Contains("Peter SIG", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Meeting_times_are_part_of_the_recorded_text()
    {
        using var h = new DraftHarness();
        h.Settings.TimeZoneLabel = "PT";
        h.Ai.MeetingTimes = true;
        h.Ai.MeetingTimeOptions = 3;
        h.FreeBusy.Calendars["me@example.org"] = [(new DateTime(2026, 11, 16, 10, 0, 0), new DateTime(2026, 11, 16, 11, 0, 0), '2')];
        h.FreeBusy.Free("ann@contoso.example");
        h.Seed("X1");
        h.Model.Enqueue(Ok("{\"needs_reply\":true,\"confidence\":0.9,\"reply_all\":false,\"body_html\":"
            + JsonSerializer.Serialize("<p>Happy to meet. These times work for me:</p><p>[[meeting_times]]</p><p>Best</p>")
            + ",\"meeting\":{\"requested\":true},\"reason\":\"r\"}"));

        await Pass(h);

        var text = h.Indexes.For("").GetAiDraftText("D1")!.Text;
        Assert.Contains("Mon 11/16", text, StringComparison.Ordinal);
        Assert.DoesNotContain("meeting_times", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_draft_records_a_row()
    {
        using var h = new DraftHarness();
        h.Ai.Enabled = false;
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1", Folder = "Inbox", Subject = "Budget", SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>", ConversationTopic = "Budget", Body = "BODYTEXT-123",
            InternetMessageId = "<E1@contoso.example>", EventTime = Now.AddMinutes(-5),
        });
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));

        await h.Worker.DraftSelectedAsync(Ct);

        var row = h.Indexes.For("").GetAiDraftText("D1")!;
        Assert.Equal("<E1@contoso.example>", row.SourceImid);
        Assert.Equal(Expected(ModelBody), row.Text);
    }

    [Fact]
    public async Task Rescan_records_a_row()
    {
        using var h = Fx();
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Budget", "", FP2);
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));

        await h.Worker.RescanAsync(Ct);

        Assert.Equal(1, Rows(h));
        Assert.Equal("<X1@contoso.example>", h.Indexes.For("").GetAiDraftText("D2")!.SourceImid);
    }

    [Fact]
    public async Task Revisit_replace_records_the_new_draft_only()
    {
        using var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = false;
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Seed("S1", at: Now - Hours(3), topic: "Ski", subject: "Ski trip", imid: "<s1@x>", attachments: []);
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - Hours(3), AiOutcome.Drafted, null, 1, 0, 0.9,
            "D0", Now - Hours(3) + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - Hours(3) + TimeSpan.FromMinutes(5), FP, Now - Hours(3));
        h.Seed("D0", at: Now - Hours(2), topic: "Ski", subject: "Re:: Ski trip", folder: "Drafts", sender: "me@example.org", attachments: []);
        h.Seed("N1", at: Now - TimeSpan.FromMinutes(30), topic: "Ski", subject: "Re: Ski trip", sender: "bob@contoso.example",
            to: "Ann <ann@contoso.example>", body: "N1-BODY", attachments: []);
        h.Mailbox.DraftInfo = (id, _) => id == "D0" ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        h.Model.Enqueue(Ok(JsonSerializer.Serialize(new Dictionary<string, object?>
            { ["action"] = "replace", ["confidence"] = 0.9, ["reason"] = "r", ["body_html"] = ModelBody })));

        await Pass(h);

        var row = h.Indexes.For("").GetAiDraftText("D2")!;
        Assert.Equal("<N1@contoso.example>", row.SourceImid);   // the reply target is the newest message
        Assert.Equal(Expected(ModelBody), row.Text);
        Assert.Null(h.Indexes.For("").GetAiDraftText("D0"));
        Assert.Equal(1, Rows(h));
    }

    [Fact]
    public async Task Shared_mailbox_draft_is_recorded_in_the_own_index()
    {
        using var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("S1", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>");
        h.Writer.Behavior = (_, _) => new RepliedDraft("DS", "Re:: Budget", DraftHarness.Shared);
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));

        await Pass(h);

        var row = h.Indexes.For("").GetAiDraftText("DS")!;
        Assert.Equal(DraftHarness.Shared, row.Mailbox);
        Assert.Equal(1, Rows(h));
        Assert.Equal(0, Rows(h, DraftHarness.Shared));
    }

    [Fact]
    public async Task No_row_when_the_setting_is_off()
    {
        using var h = Fx();
        h.Ai.LearnFromEdits = false;
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));
        await Pass(h);
        Assert.Single(h.Writer.Plans);
        Assert.Equal(0, Rows(h));
    }

    [Fact]
    public async Task No_row_without_an_imid()
    {
        using var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Seed("X1", imid: "");
        h.Mailbox.Facts = (_, _) => FakeAiMailbox.DefaultFacts with { InternetMessageId = null };
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));
        await Pass(h);
        Assert.Single(h.Writer.Plans);
        Assert.Equal(0, Rows(h));
    }

    private static DraftHarness AttFx(out FakeAttachmentSource src, string text)
    {
        src = new FakeAttachmentSource();
        var h = new DraftHarness(attachments: src);
        h.Ai.MeetingTimes = false;
        h.Ai.ReadAttachments = true;
        h.Ai.AttachmentsNoticeShown = true;
        h.Seed("X1", attachments: [new AttachmentInfo(1, "notes.txt", 40, false)]);
        src.Returns("X1", 1, new AiAttachmentRead.File(Encoding.UTF8.GetBytes(text)));
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));
        return h;
    }

    [Fact]
    public async Task No_row_when_an_attachment_text_was_read()
    {
        using var h = AttFx(out _, "budget notes");
        await Pass(h);
        Assert.Single(h.Writer.Plans);
        Assert.Contains(h.Log.Lines, l => l.Contains(" 1 read,", StringComparison.Ordinal));
        Assert.Equal(0, Rows(h));
    }

    [Fact]
    public async Task No_row_when_an_attachment_text_was_truncated()
    {
        using var h = AttFx(out _, string.Concat(Enumerable.Repeat("word ", 400_000)));
        await Pass(h);
        Assert.Single(h.Writer.Plans);
        Assert.Contains(h.Log.Lines, l => l.Contains(" 1 truncated,", StringComparison.Ordinal));
        Assert.Equal(0, Rows(h));
    }

    [Fact]
    public async Task Row_written_when_every_attachment_was_skipped()
    {
        using var h = AttFx(out var src, "x");
        src.Returns("X1", 1, null);
        await Pass(h);
        Assert.Single(h.Writer.Plans);
        Assert.Contains(h.Log.Lines, l => l.Contains(" 0 read, 0 truncated, 1 skipped,", StringComparison.Ordinal));
        Assert.Equal(1, Rows(h));
    }

    [Fact]
    public async Task A_failing_index_write_never_affects_the_draft()
    {
        using var h = Fx();
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));
        h.Indexes.For("");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = MailboxIndexes.PathFor(h.Dir, ""), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE ai_draft_texts";
            cmd.ExecuteNonQuery();
        }

        await Pass(h);

        Assert.Single(h.Writer.Plans);
        Assert.Equal(AiOutcome.Drafted, h.Decision("X1")!.Outcome);
        Assert.Single(h.Events(ActivityKind.Draft));
        Assert.Single(h.Log.Lines, l => l == "AI drafts: couldn't record a draft's text (SqliteException).");
    }

    [Fact]
    public async Task Logs_and_events_hold_no_text_and_no_imid()
    {
        using var h = Fx();
        h.Model.Enqueue(OkReply(0.9, body: ModelBody));
        await Pass(h);
        Assert.Equal(1, Rows(h));
        Assert.DoesNotContain(h.AllText(), t => t.Contains(BodyWord, StringComparison.Ordinal));
        Assert.DoesNotContain(h.AllText(), t => t.Contains("X1@contoso", StringComparison.Ordinal));
    }
}

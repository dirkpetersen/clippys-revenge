using System.Text.Json;
using Microsoft.Data.Sqlite;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerRelatedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;

    private const string Marker = "RELATED-MARKER-55";
    private const string SentMarker = "SENT-MARKER-12";
    private const string ThreadOnly = "THREAD-ONLY-77";

    private static string Block(string prompt)
    {
        var s = prompt.IndexOf("<related_mail>", StringComparison.Ordinal);
        var e = prompt.IndexOf("</related_mail>", StringComparison.Ordinal);
        return s < 0 || e < 0 ? "" : prompt[s..e];
    }

    private static string RelatedNote => DraftPrompt.RelatedNote.Replace("{name}", "Dirk Example");

    /// <summary>The common seed: candidate C1, thread row T1 (older than the enable time, so no candidate), related R1.</summary>
    private static DraftHarness Fx(bool related = true, bool noticeShown = true, bool sent = false, bool withR1 = true)
    {
        var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RelatedMail = related;
        h.Ai.RelatedMailNoticeShown = noticeShown;
        h.Seed("C1", subject: "Budget planning", topic: "Budget");
        h.Seed("T1", age: TimeSpan.FromMinutes(90), subject: "Re: Budget planning", topic: "Budget", body: ThreadOnly);
        if (withR1)
            h.Seed("R1", age: TimeSpan.FromDays(30), subject: "Budget planning offsite", topic: "Offsite",
                sender: "bob@contoso.example", body: Marker);
        if (sent)
            h.Seed("S1", folder: "Sent Items", age: TimeSpan.FromDays(60), subject: "Lunch", topic: "Lunch",
                sender: "me@example.org", to: "Ann <ann@contoso.example>", body: SentMarker);
        return h;
    }

    private static Task<DraftPassResult> Pass(DraftHarness h) => h.Worker.RunPassAsync(Ct);

    // 39
    [Fact]
    public async Task Automatic_path_puts_related_mail_in_the_prompt_and_the_note_in_the_system_prompt()
    {
        using var h = Fx();
        h.Model.Enqueue(OkReply(0.9));

        await Pass(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(1, req.UserPrompt.Split("<related_mail>").Length - 1);
        Assert.Contains(Marker, Block(req.UserPrompt));
        Assert.DoesNotContain(ThreadOnly, Block(req.UserPrompt));
        Assert.Contains(ThreadOnly, req.UserPrompt);
        Assert.Contains(RelatedNote, req.SystemPrompt);
        Assert.Contains("AI drafts: related mail 1 (2 terms)", h.Log.Lines);
    }

    // 40
    [Fact]
    public async Task Own_earlier_reply_to_the_sender_is_included()
    {
        using var h = Fx(sent: true);
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var block = Block(Assert.Single(h.Model.Requests).UserPrompt);
        Assert.Contains(SentMarker, block);
        Assert.Contains(Marker, block);
        Assert.Contains("folder=\"Sent Items\"", block);
    }

    // 41
    [Fact]
    public async Task Setting_off_leaves_the_prompts_alone()
    {
        using var h = Fx(related: false, noticeShown: false);
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var req = Assert.Single(h.Model.Requests);
        Assert.DoesNotContain("<related_mail", req.UserPrompt);
        Assert.DoesNotContain("related_mail", req.SystemPrompt);
        Assert.Equal(DraftPrompt.SystemPrompt("Dirk Example", "me@example.org", false, "STYLE", false, h.Facts.ReadForPrompt()),
            req.SystemPrompt);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("related mail", StringComparison.Ordinal));
        Assert.Empty(h.NoticeMarks);
        Assert.DoesNotContain(h.Events(ActivityKind.Ai), e => e.Summary == RelatedMailTexts.Notice);
    }

    // 42
    [Fact]
    public async Task Nothing_related_means_no_block_and_no_note()
    {
        using var h = Fx(withR1: false);
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var req = Assert.Single(h.Model.Requests);
        Assert.DoesNotContain("<related_mail", req.UserPrompt);
        Assert.DoesNotContain("<related_mail>", req.SystemPrompt);
        Assert.Contains("AI drafts: related mail 0 (2 terms)", h.Log.Lines);
    }

    // 43
    [Fact]
    public async Task Manual_path_gets_the_block_and_the_note()
    {
        using var h = Fx(withR1: true);
        h.Ai.Enabled = false;
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1",
            Folder = "Inbox",
            Subject = "Budget planning",
            SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>",
            ConversationTopic = "Budget",
            Body = "BODYTEXT-123",
            EventTime = Now.AddMinutes(-5),
        });
        h.Model.Enqueue(OkReply(0.9));

        await h.Worker.DraftSelectedAsync(Ct);

        var req = Assert.Single(h.Model.Requests);
        Assert.Contains(Marker, Block(req.UserPrompt));
        Assert.Contains(RelatedNote, req.SystemPrompt);
    }

    // 44
    [Fact]
    public async Task Revisit_path_gets_related_mail_but_nothing_from_the_drafts_thread()
    {
        using var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = false;
        h.Ai.RelatedMail = true;
        h.Ai.RelatedMailNoticeShown = true;
        var h3 = TimeSpan.FromHours(3);
        h.Seed("S1", at: Now - h3, topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Seed("S0", age: TimeSpan.FromDays(10), topic: "Ski", subject: "Ski trip earlier", body: "SKI-THREAD-ONLY-11");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - h3, AiOutcome.Drafted, null, 1, 0, 0.9, "D0",
            Now - h3 + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - h3 + TimeSpan.FromMinutes(5), FP, Now - h3);
        h.Seed("N1", at: Now - TimeSpan.FromMinutes(30), topic: "Ski", subject: "Re: Ski trip", sender: "bob@contoso.example",
            to: "Ann <ann@contoso.example>", body: "N1-BODY");
        h.Seed("R1", age: TimeSpan.FromDays(40), topic: "Old", subject: "Ski trip history", sender: "bob@contoso.example", body: Marker);
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        h.Model.Enqueue(Ok(JsonSerializer.Serialize(new Dictionary<string, object> { ["action"] = "keep", ["confidence"] = 0.9, ["reason"] = "r" })));

        await Pass(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.RevisitSchema, req.JsonSchema);
        var block = Block(req.UserPrompt);
        Assert.Contains(Marker, block);
        Assert.DoesNotContain("SKI-THREAD-ONLY-11", block);
        Assert.DoesNotContain("N1-BODY", block);
        Assert.Contains("SKI-THREAD-ONLY-11", req.UserPrompt);
        Assert.Contains(RelatedNote, req.SystemPrompt);
    }

    // 45
    [Fact]
    public async Task Rescan_path_gets_the_block()
    {
        using var h = new DraftHarness();
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Ai.MeetingTimes = false;
        h.Ai.RelatedMail = true;
        h.Ai.RelatedMailNoticeShown = true;
        h.Seed("A", at: Now - TimeSpan.FromHours(3), subject: "Budget planning", topic: "Budget");
        h.Seed("R1", age: TimeSpan.FromDays(30), subject: "Budget planning offsite", topic: "Offsite", sender: "bob@contoso.example", body: Marker);
        h.Model.OnRun = (_, _) => Task.FromResult(OkReply(0.9, needsReply: false));

        await h.Worker.RescanAsync(Ct);

        var req = Assert.Single(h.Model.Requests);
        Assert.Contains(Marker, Block(req.UserPrompt));
        Assert.Contains(RelatedNote, req.SystemPrompt);
    }

    // 46
    [Fact]
    public async Task Only_the_anchors_own_mailbox_index_is_searched()
    {
        using var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RelatedMail = true;
        h.Ai.RelatedMailNoticeShown = true;
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("SC1", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>", subject: "Budget planning", topic: "Budget");
        h.Seed("R1", age: TimeSpan.FromDays(30), subject: "Budget planning offsite", topic: "Offsite", sender: "bob@contoso.example", body: Marker);
        h.Model.Enqueue(OkReply(0.9));

        await Pass(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Contains("mailbox: shared " + DraftHarness.Shared, req.UserPrompt);
        Assert.DoesNotContain("<related_mail", req.UserPrompt);
        Assert.DoesNotContain(Marker, req.UserPrompt);
    }

    // 47
    [Fact]
    public async Task A_failing_lookup_still_drafts_without_a_block()
    {
        using var h = Fx();
        var cs = new SqliteConnectionStringBuilder { DataSource = MailboxIndexes.PathFor(h.Dir, ""), Pooling = false }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TRIGGER emails_ai; DROP TRIGGER emails_ad; DROP TRIGGER emails_au; DROP TABLE emails_fts;";
            cmd.ExecuteNonQuery();
        }
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(1, r.Drafted);
        var req = Assert.Single(h.Model.Requests);
        Assert.DoesNotContain("<related_mail", req.UserPrompt);
        Assert.DoesNotContain("<related_mail>", req.SystemPrompt);
        Assert.Single(h.Log.Lines, l => l == "AI drafts: looking up related mail failed (SqliteException).");
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains(Marker, StringComparison.Ordinal));
    }

    // 48
    [Fact]
    public async Task Notice_is_shown_once_before_the_first_model_call()
    {
        using var h = Fx(noticeShown: false);
        h.Model.OnRun = (_, n) =>
        {
            if (n == 1)
            {
                Assert.Equal(1, h.Events(ActivityKind.Ai).Count(e => e.Summary == RelatedMailTexts.Notice));
                Assert.Single(h.Shown, b => b.Text == RelatedMailTexts.Notice);
                Assert.Empty(h.Events(ActivityKind.Draft));
            }
            return Task.FromResult(OkReply(0.9));
        };

        await Pass(h);

        Assert.Equal([AiNotice.RelatedMail], h.NoticeMarks);
        var balloon = Assert.Single(h.Shown, b => b.Text == RelatedMailTexts.Notice);
        Assert.Equal(BalloonKind.Info, balloon.Kind);

        h.Seed("C2", subject: "Other thing", topic: "Other");
        await Pass(h);
        Assert.Equal(1, h.Events(ActivityKind.Ai).Count(e => e.Summary == RelatedMailTexts.Notice));
        Assert.Equal([AiNotice.RelatedMail], h.NoticeMarks);
    }

    [Fact]
    public async Task Notice_is_not_shown_when_already_shown_or_the_setting_is_off()
    {
        using var shown = Fx(noticeShown: true);
        shown.Model.Enqueue(OkReply(0.9));
        await Pass(shown);
        Assert.Empty(shown.NoticeMarks);
        Assert.DoesNotContain(shown.Events(ActivityKind.Ai), e => e.Summary == RelatedMailTexts.Notice);

        using var off = Fx(related: false, noticeShown: false);
        off.Model.Enqueue(OkReply(0.9));
        await Pass(off);
        Assert.Empty(off.NoticeMarks);
        Assert.DoesNotContain(off.Events(ActivityKind.Ai), e => e.Summary == RelatedMailTexts.Notice);
    }

    // 49
    [Fact]
    public async Task Notice_save_failure_is_logged_and_the_notice_still_shows()
    {
        using var h = Fx(noticeShown: false);
        h.MarkNoticeShown = _ => throw new IOException("disk full");
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(1, r.Drafted);
        Assert.Single(h.Writer.Plans);
        Assert.Equal(1, h.Events(ActivityKind.Ai).Count(e => e.Summary == RelatedMailTexts.Notice));
        Assert.Single(h.Shown, b => b.Text == RelatedMailTexts.Notice);
        Assert.Single(h.Log.Lines,
            l => l == "AI drafts: couldn't save that the related-mail notice was shown (IOException).");
    }

    // 50
    [Fact]
    public async Task Related_mail_never_reaches_logs_events_or_the_decision_tables()
    {
        using var h = Fx(sent: true, noticeShown: false);
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        Assert.Contains(Marker, Assert.Single(h.Model.Requests).UserPrompt);

        var all = string.Join("\n", h.AllText());
        foreach (var secret in new[] { Marker, SentMarker, "offsite", "bob@contoso.example" })
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);

        var cs = new SqliteConnectionStringBuilder { DataSource = MailboxIndexes.PathFor(h.Dir, ""), Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        foreach (var table in new[] { "ai_decisions", "ai_drafts", "ai_revisits", "ai_calls" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM " + table;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                for (var i = 0; i < rd.FieldCount; i++)
                {
                    var v = Convert.ToString(rd.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    foreach (var secret in new[] { Marker, SentMarker, "offsite", "bob@contoso.example" })
                        Assert.DoesNotContain(secret, v, StringComparison.OrdinalIgnoreCase);
                }
        }
    }

    // 51
    [Fact]
    public async Task Without_a_persistence_callback_the_notice_is_shown_once_and_nothing_throws()
    {
        using var h = new DraftHarness(withMarkNoticeShown: false);
        h.Ai.MeetingTimes = false;
        h.Ai.RelatedMail = true;
        h.Ai.RelatedMailNoticeShown = false;
        h.Seed("C1", subject: "Budget planning", topic: "Budget");
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        Assert.Equal(1, h.Events(ActivityKind.Ai).Count(e => e.Summary == RelatedMailTexts.Notice));

        h.Seed("C2", subject: "Other thing", topic: "Other");
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        Assert.Equal(1, h.Events(ActivityKind.Ai).Count(e => e.Summary == RelatedMailTexts.Notice));
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerReasonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Note = "Ann asked about the budget; this answers that.";
    private const string Secret = "NOTE-SECRET-91";
    private const string OldNote = "OLD-NOTE-77";
    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;
    private static readonly TimeSpan H3 = TimeSpan.FromHours(3);

    private static string L(string? note) => DraftReasonLine.Html(note);
    private static readonly string Body = DraftHtml.Sanitize("<p>Hi [[date]]</p>");

    private static DraftHarness Fx(bool on = true)
    {
        var h = new DraftHarness();
        h.Ai.ReasonLine = on;
        h.Ai.MeetingTimes = false;
        return h;
    }

    private static Task<DraftPassResult> Pass(DraftHarness h) => h.Worker.RunPassAsync(Ct);

    private static ModelResult Reply(string? noteJson = null, double confidence = 0.9, bool needsReply = true,
        string body = "<p>Hi [[date]]</p>")
    {
        var note = noteJson is null ? "" : ",\"note_for_user\":" + noteJson;
        return Ok("{\"needs_reply\":" + (needsReply ? "true" : "false") + ",\"confidence\":" + confidence.ToString("0.###", CultureInfo.InvariantCulture)
            + ",\"body_html\":" + JsonSerializer.Serialize(body) + ",\"reason\":\"SECRET-REASON\"" + note + "}");
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private static string Fp(string fragment) => DraftFingerprint.Compute(new DraftFingerprintInput("Re:: Budget",
        "Ann <ann@contoso.example>", null, null, HtmlBody.InsertAtTop("<html><body></body></html>", fragment), "", 1, null, 0, null));

    // 31
    [Fact]
    public async Task Automatic_path_prepends_the_line_and_asks_for_it()
    {
        using var h = Fx();
        h.Seed("C1");
        h.Model.Enqueue(Reply(Json(Note)));

        await Pass(h);

        Assert.Equal(L(Note) + Body, Assert.Single(h.Writer.Plans).HtmlFragment);
        var req = Assert.Single(h.Model.Requests);
        Assert.Contains(DraftPrompt.ReasonNote.Replace("{name}", "Dirk Example"), req.SystemPrompt);
        Assert.Equal(DraftModel.ReplySchema, req.JsonSchema);
        Assert.Contains("AI drafts: note: yes", h.Log.Lines);
    }

    // 32
    [Theory]
    [InlineData(null)]
    [InlineData("\"   \"")]
    [InlineData("7")]
    public async Task No_usable_note_means_no_line(string? noteJson)
    {
        using var h = Fx();
        h.Seed("C1");
        h.Model.Enqueue(Reply(noteJson));

        await Pass(h);

        Assert.Equal(Body, Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Contains(DraftPrompt.ReasonNote.Replace("{name}", "Dirk Example"), Assert.Single(h.Model.Requests).SystemPrompt);
        Assert.Contains("AI drafts: note: no", h.Log.Lines);
    }

    // 33
    [Fact]
    public async Task Setting_off_adds_no_line_no_prompt_text_no_log()
    {
        using var h = Fx(on: false);
        h.Seed("C1");
        h.Model.Enqueue(Reply(Json(Note)));

        await Pass(h);

        Assert.Equal(Body, Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Equal(DraftPrompt.SystemPrompt("Dirk Example", "me@example.org", false, "STYLE", false, h.Facts.ReadForPrompt()),
            Assert.Single(h.Model.Requests).SystemPrompt);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("AI drafts: note", StringComparison.Ordinal));
    }

    // 34
    [Fact]
    public async Task The_line_sits_above_the_body_and_the_signature_below()
    {
        using var h = Fx();
        h.SignatureHtml = "<html><body><p>SIG-1</p></body></html>";
        h.Seed("C1");
        h.Model.Enqueue(Reply(Json(Note)));

        await Pass(h);

        Assert.Equal(L(Note) + Body + "<br>" + "<p>SIG-1</p>", Assert.Single(h.Writer.Plans).HtmlFragment);
    }

    // 35
    [Fact]
    public async Task Meeting_offers_come_after_the_line_and_the_line_is_not_seen_by_the_meeting_code()
    {
        using var h = Fx();
        h.Settings.TimeZoneLabel = "PT";
        h.Ai.MeetingTimes = true;
        h.Ai.MeetingTimeOptions = 3;
        h.FreeBusy.Calendars["me@example.org"] = [(new DateTime(2026, 11, 16, 10, 0, 0), new DateTime(2026, 11, 16, 11, 0, 0), '2')];
        h.FreeBusy.Free("ann@contoso.example");
        h.Seed("C1");
        h.Model.Enqueue(Ok("{\"needs_reply\":true,\"confidence\":0.9,\"reply_all\":false,\"body_html\":"
            + Json("<p>Happy to meet. These times work for me:</p><p>[[meeting_times]]</p><p>Best, Dirk</p>")
            + ",\"meeting\":{\"requested\":true},\"reason\":\"r\",\"note_for_user\":" + Json(Note) + "}"));

        await Pass(h);

        var f = Assert.Single(h.Writer.Plans).HtmlFragment;
        Assert.StartsWith(L(Note), f);
        var lineEnd = f.IndexOf("</span></p>", StringComparison.Ordinal);
        Assert.True(lineEnd > 0);
        var offer = f.IndexOf("1. Mon 11/16", StringComparison.Ordinal);
        Assert.True(offer > lineEnd);
        Assert.DoesNotContain("meeting_times", f);
    }

    // 36
    [Fact]
    public async Task Manual_path_prepends_the_line()
    {
        using var h = Fx();
        h.Ai.Enabled = false;
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1", Folder = "Inbox", Subject = "Budget", SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>", ConversationTopic = "Budget", Body = "BODYTEXT-123", EventTime = Now.AddMinutes(-5),
        });
        h.Model.Enqueue(Reply(Json(Note)));

        var r = await h.Worker.DraftSelectedAsync(Ct);

        Assert.True(r.Drafted);
        Assert.StartsWith(L(Note), Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Contains(DraftPrompt.ReasonNote.Replace("{name}", "Dirk Example"), Assert.Single(h.Model.Requests).SystemPrompt);
    }

    // 37
    [Fact]
    public async Task Rescan_path_prepends_the_line()
    {
        using var h = Fx();
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Seed("A", at: Now - TimeSpan.FromHours(2), topic: "Ski", subject: "Ski");
        h.Model.Enqueue(Reply(Json(Note)));

        await h.Worker.RescanAsync(Ct);

        Assert.StartsWith(L(Note), Assert.Single(h.Writer.Plans).HtmlFragment);
    }

    private static DraftHarness FxRevisit()
    {
        var h = Fx();
        h.Ai.RequireDirectRecipient = false;
        h.Seed("S1", at: Now - H3, topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - H3, AiOutcome.Drafted, null, 1, 0, 0.9, "D0",
            Now - H3 + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - H3 + TimeSpan.FromMinutes(5), FP, Now - H3);
        h.Seed("N1", at: Now - TimeSpan.FromMinutes(30), topic: "Ski", subject: "Re: Ski trip",
            sender: "bob@contoso.example", to: "Ann <ann@contoso.example>", body: "N1-BODY");
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "Clippy: " + OldNote + "\n\nOld draft text.") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        return h;
    }

    // 38
    [Fact]
    public async Task Revisit_replace_gets_a_new_line_and_does_not_show_the_old_one_to_the_model()
    {
        using var h = FxRevisit();
        const string note = "Bob added a date; this answers both.";
        h.Model.Enqueue(Ok("{\"action\":\"replace\",\"confidence\":0.9,\"reason\":\"r\",\"body_html\":\"<p>New</p>\",\"note_for_user\":" + Json(note) + "}"));

        await Pass(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.RevisitSchema, req.JsonSchema);
        Assert.Contains(DraftPrompt.ReasonNote.Replace("{name}", "Dirk Example"), req.SystemPrompt);
        Assert.Contains("Old draft text.", req.UserPrompt);
        Assert.DoesNotContain(OldNote, req.UserPrompt);
        Assert.Equal(L(note) + DraftHtml.Sanitize("<p>New</p>"), Assert.Single(h.Writer.Plans).HtmlFragment);
    }

    // 39
    [Fact]
    public async Task Revisit_keep_makes_no_line_and_no_log()
    {
        using var h = FxRevisit();
        h.Model.Enqueue(Ok("{\"action\":\"keep\",\"confidence\":0.9,\"reason\":\"r\",\"note_for_user\":\"n\"}"));

        await Pass(h);

        Assert.Empty(h.Writer.Plans);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("AI drafts: note", StringComparison.Ordinal));
    }

    // 40
    [Theory]
    [InlineData(0.9, false)]
    [InlineData(0.1, true)]
    public async Task No_draft_means_no_line_and_no_log(double confidence, bool needsReply)
    {
        using var h = Fx();
        h.Seed("C1");
        h.Model.Enqueue(Reply(Json(Note), confidence, needsReply));

        await Pass(h);

        Assert.Empty(h.Writer.Plans);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("AI drafts: note", StringComparison.Ordinal));
    }

    // 41
    [Fact]
    public async Task A_hostile_note_cannot_add_markup_or_links()
    {
        using var h = Fx();
        h.Seed("C1");
        const string hostile = "<a href=\"https://evil.example/x\">click</a> & www.evil.example <script>x</script>";
        h.Model.Enqueue(Reply(Json(hostile)));

        await Pass(h);

        var f = Assert.Single(h.Writer.Plans).HtmlFragment;
        Assert.StartsWith("<p>" + DraftHtml.HighlightOpen + "Clippy: ", f);
        var line = f[..^Body.Length];
        Assert.DoesNotContain("<a", line);
        Assert.DoesNotContain("<script", line);
        Assert.DoesNotContain("evil.example", line);
        Assert.DoesNotContain("://", line);
        Assert.Equal(L(hostile), line);
        Assert.Contains("&lt;a href=&quot;[link] &amp; [link] &lt;script&gt;x&lt;/script&gt;", line);
    }

    private static string DbText(DraftHarness h)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = MailboxIndexes.PathFor(h.Dir, ""), Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "ai_decisions", "ai_drafts", "ai_revisits", "ai_calls" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM " + table;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++) sb.Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
        }
        return sb.ToString();
    }

    // 42
    [Fact]
    public async Task The_note_is_never_logged_or_stored()
    {
        using var h = FxRevisit();
        h.Seed("C1", age: TimeSpan.FromMinutes(5), topic: "Other", subject: "Other");
        h.Model.OnRun = (req, _) => Task.FromResult(req.JsonSchema == DraftModel.RevisitSchema
            ? Ok("{\"action\":\"replace\",\"confidence\":0.9,\"reason\":\"r\",\"body_html\":\"<p>New</p>\",\"note_for_user\":\"" + Secret + "\"}")
            : Reply(Json(Secret)));
        await Pass(h);
        Assert.NotEmpty(h.Writer.Plans);

        Assert.DoesNotContain(h.AllText(), t => t.Contains(Secret, StringComparison.Ordinal) || t.Contains(OldNote, StringComparison.Ordinal));
        var db = DbText(h);
        Assert.DoesNotContain(Secret, db);
        Assert.DoesNotContain(OldNote, db);
    }

    // 43
    [Fact]
    public async Task The_saved_fingerprint_covers_the_line()
    {
        using var h = Fx();
        h.Seed("C1");
        h.Writer.Behavior = (plan, _) => new RepliedDraft("D1", "Re:: Budget", "", Fp(plan.HtmlFragment));
        h.Model.Enqueue(Reply(Json(Note)));

        await Pass(h);

        var d = h.Indexes.For("").GetAiDraft("D1")!;
        Assert.Equal(Fp(L(Note) + Body), d.Fingerprint);
        Assert.NotEqual(Fp(Body), d.Fingerprint);
    }

    // 44
    [Fact]
    public async Task A_draft_whose_line_was_deleted_counts_as_edited_and_is_left_alone()
    {
        var withLine = Fp(L(Note) + Body);
        using var h = FxRevisit();
        h.Indexes.For("").RecordAiDraft("D0", "S1", Now - H3, withLine, Now - H3, Now - H3);
        h.Mailbox.DraftInfo = (id, _) => id == "D0" ? FakeAiMailbox.Draft(Fp(Body)) : null;
        h.Model.Enqueue(Ok("{\"action\":\"replace\",\"confidence\":0.9,\"reason\":\"r\",\"body_html\":\"<p>New</p>\"}"));

        await Pass(h);

        Assert.DoesNotContain(h.Model.Requests, r => r.JsonSchema == DraftModel.RevisitSchema);
        Assert.Equal(AiDraftState.Edited, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Empty(h.Mailbox.Moves);

        // Control: the line still there, so the revisit runs.
        using var c = FxRevisit();
        c.Indexes.For("").RecordAiDraft("D0", "S1", Now - H3, withLine, Now - H3, Now - H3);
        c.Mailbox.DraftInfo = (id, _) => id == "D0" ? FakeAiMailbox.Draft(withLine) : null;
        c.Model.Enqueue(Ok("{\"action\":\"keep\",\"confidence\":0.9,\"reason\":\"r\"}"));
        await Pass(c);
        Assert.Contains(c.Model.Requests, r => r.JsonSchema == DraftModel.RevisitSchema);
    }
}
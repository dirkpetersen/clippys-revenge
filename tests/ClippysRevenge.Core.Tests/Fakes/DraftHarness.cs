using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

internal sealed class CapturingLog : ILogger
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _lines.Add(formatter(state, exception));
            if (exception is not null) _lines.Add(exception.ToString());
        }
    }
}

/// <summary>Real indexes, hub and balloons; fake mailbox, model, writer and style. Clock is manual (UTC 2026-11-13 20:00, zone UTC-8).</summary>
internal sealed class DraftHarness : IDisposable
{
    public const string Shared = "helpdesk@example.org";
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-13T20:00:00Z");
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("T-8", TimeSpan.FromHours(-8), "T-8", "T-8");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-draft-" + Guid.NewGuid().ToString("N"));

    public ManualTimeProvider Time { get; } = new();
    public MailboxIndexes Indexes { get; }
    public Settings Settings { get; } = new();
    public ActivityHub Hub { get; }
    public BalloonCenter Balloons { get; }
    public FakeAiMailbox Mailbox { get; } = new();
    public FakeDraftModel Model { get; } = new();
    public FakeReplyWriter Writer { get; } = new();
    public FakeStyleGuide Style { get; } = new();
    public FakeFacts Facts { get; } = new();
    public CapturingLog Log { get; } = new();
    public List<Balloon> Shown { get; } = [];
    public bool Available { get; set; } = true;
    public bool Paused { get; set; }
    public string? SignatureHtml { get; set; }
    public FakeFreeBusySource FreeBusy { get; } = new();
    public Func<TimeZoneInfo?> Cet { get; set; } = TestZones.Berlin;
    public DraftWorker Worker { get; }
    public List<AiNotice> NoticeMarks { get; } = [];
    public Action<AiNotice>? MarkNoticeShown { get; set; }

    public DraftHarness(bool withMarkNoticeShown = true, FakeAttachmentSource? attachments = null)
    {
        Time.Advance(Start - ManualTimeProvider.T0);
        Indexes = new MailboxIndexes(_dir, [Shared]);
        Hub = new ActivityHub(Time);
        Balloons = new BalloonCenter(Time);
        Balloons.Shown += Shown.Add;
        Settings.AiDrafting.RecordConsent(Start.AddHours(-2));
        Settings.AiDrafting.SetEnabled(true, Start.AddHours(-1));
        Settings.AiDrafting.MaxAgeDays = 2;
        // Existing worker tests stay byte-identical; the related-mail tests turn it on.
        Settings.AiDrafting.RelatedMail = false;
        Settings.AiDrafting.ReasonLine = false;
        Settings.AiDrafting.RelatedMailNoticeShown = true;
        Settings.AiDrafting.AttachmentsNoticeShown = true;
        Settings.AiDrafting.EditsNoticeShown = true;
        Settings.SharedMailboxes.Add(new SharedMailbox { Smtp = Shared, Enabled = true, AiDrafts = false });
        if (withMarkNoticeShown) MarkNoticeShown = NoticeMarks.Add;
        Worker = new DraftWorker(new DraftWorkerServices(() => Settings, Indexes, Mailbox, Writer, Model, Style,
            () => SignatureHtml, Hub, Balloons, () => Available, () => Paused, Zone, Time, Log,
            FreeBusy: FreeBusy, CentralEurope: () => Cet(), Facts: Facts,
            MarkNoticeShown: withMarkNoticeShown ? n => MarkNoticeShown?.Invoke(n) : null,
            Attachments: attachments is null ? null : new DraftAttachments(attachments, Time, Log)));
    }

    public AiDraftingSettings Ai => Settings.AiDrafting;

    public static DateTimeOffset Now => Start;

    public void Dispose()
    {
        Hub.Dispose();
        Indexes.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Seeds one row. Default: Inbox of the own mailbox, 10 minutes old, from ann@contoso.example, to me@example.org.</summary>
    public IndexedEmail Seed(string id, string mailbox = "", string folder = "Inbox", TimeSpan? age = null, DateTimeOffset? at = null,
        string sender = "ann@contoso.example", string subject = "Budget", string topic = "Budget", string? imid = null,
        string to = "Me <me@example.org>", string body = "BODYTEXT-123", string? irt = null, string? cc = null, IReadOnlyList<AttachmentInfo>? attachments = null)
    {
        var e = new IndexedEmail
        {
            EntryId = id,
            Folder = folder,
            Subject = subject,
            SenderName = "Ann",
            SenderEmail = sender,
            To = to,
            Cc = cc,
            Body = body,
            ConversationTopic = topic,
            InternetMessageId = imid ?? "<" + id + "@contoso.example>",
            InReplyTo = irt,
            EventTime = at ?? Time.GetUtcNow() - (age ?? TimeSpan.FromMinutes(10)),
            Attachments = attachments ?? [new AttachmentInfo(1, "a.pdf", 10, false)],
        };
        Indexes.For(mailbox).UpsertBatch([e]);
        return e;
    }

    public string Dir => _dir;

    public AiDecision? Decision(string id, string mailbox = "") => Indexes.For(mailbox).GetDecision(id);

    /// <summary>The ai_revisits rows of a mailbox, oldest first.</summary>
    public IReadOnlyList<(string DraftId, string Outcome, int Calls)> RevisitRows(string mailbox = "")
    {
        Indexes.For(mailbox);
        var rows = new List<(string, string, int)>();
        var cs = new SqliteConnectionStringBuilder { DataSource = MailboxIndexes.PathFor(Dir, mailbox), Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT draft_entry_id, outcome, model_calls FROM ai_revisits ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return rows;
    }

    /// <summary>Tracks an AI draft in ai_drafts (state open).</summary>
    public void Track(string draftId, string sourceId, DateTimeOffset created, string fingerprint, DateTimeOffset checkedUpTo,
        string mailbox = "") =>
        Indexes.For(mailbox).RecordAiDraft(draftId, sourceId, created, fingerprint, checkedUpTo, created);

    public IReadOnlyList<ActivityEvent> Events(ActivityKind kind) => Hub.GetEvents().Where(e => e.Kind == kind).ToList();

    public IEnumerable<string> AllText() =>
        Hub.GetEvents().Select(e => e.Summary).Concat(Log.Lines);
}

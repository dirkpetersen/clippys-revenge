using Microsoft.Data.Sqlite;
using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class DontDraftServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-dd-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly List<Settings> _saves = [];
    private readonly FakeAiMailbox _mb = new();
    private readonly MailboxIndexes _indexes;
    private readonly ActivityHub _hub = new(new ManualTimeProvider());
    private readonly CapturingLog _log = new();
    private readonly DontDraftService _svc;
    private int _changed;
    private Exception? _saveThrows;

    public DontDraftServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = Settings.CreateDefault(_dir);
        _indexes = new MailboxIndexes(Path.Combine(_dir, "idx"), []);
        var toggler = new AiDraftingToggler(_settings, s =>
        {
            if (_saveThrows is not null) throw _saveThrows;
            _saves.Add(s.Clone());
        }, () => _changed++);
        _svc = new DontDraftService(_mb, _indexes, toggler, () => _settings.AiDrafting, _hub, _log);
        _mb.Selected = new SelectedEmail("E1", null);
        Sender("ann@contoso.example");
    }

    public void Dispose()
    {
        _hub.Dispose();
        _indexes.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Sender(string address, string mailbox = "") =>
        _mb.Live = new LiveEmail(mailbox, new IndexedEmail
        {
            EntryId = "E1", Folder = "Inbox", SenderEmail = address, EventTime = DateTimeOffset.Parse("2026-11-13T20:00:00Z"),
        });

    private Task<DontDraftPrepared> Prep(DontDraftKind k = DontDraftKind.Sender) => _svc.PrepareAsync(k, CancellationToken.None);

    private void Fill(int n) =>
        _settings.AiDrafting.ExcludeSenders = Enumerable.Range(0, n).Select(i => "u" + i + "@other.example").ToList();

    [Fact]
    public async Task No_selection()
    {
        _mb.Selected = null;
        Assert.Equal("Select one email in Outlook first.", (await Prep()).Balloon);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Not_in_mailbox()
    {
        _mb.Live = null;
        Assert.Equal("That email isn't in your mailbox or an enabled shared mailbox.", (await Prep()).Balloon);
    }

    [Fact]
    public async Task Outlook_unavailable()
    {
        _mb.SelectedThrow = new OutlookUnavailableException();
        Assert.Equal(BalloonTexts.OutlookUnavailable, (await Prep()).Balloon);
    }

    [Fact]
    public async Task No_smtp_address()
    {
        Sender("/O=EXCHANGELABS/OU=X/CN=ANN");
        Assert.Equal(DontDraftTexts.NoAddress, (await Prep()).Balloon);
    }

    [Fact]
    public async Task Owner_unknown_then_known_through_meta()
    {
        _mb.Identity = new AiIdentity("", "");
        Assert.Equal(AiTabTexts.OwnerUnknown, (await Prep()).Balloon);
        _indexes.For(MailboxIndexes.OwnMailbox).SetMeta("mailbox_smtp", "me@example.org");
        Assert.Equal("ann@contoso.example", (await Prep()).Entry);
    }

    [Fact]
    public async Task Own_address()
    {
        Sender("me@example.org");
        Assert.Equal(DontDraftTexts.OwnAddress, (await Prep()).Balloon);
        Sender("helpdesk@example.org", "helpdesk@example.org");
        Assert.Equal(DontDraftTexts.OwnAddress, (await Prep()).Balloon);
    }

    [Fact]
    public async Task Sender_happy_path()
    {
        var p = await Prep();
        Assert.Equal("ann@contoso.example", p.Entry);
        Assert.False(p.NeedsConfirm);
        Assert.Equal("I won't draft replies to ann@contoso.example any more. You can undo this in Settings → AI drafts.", _svc.Apply(p));
        Assert.Equal(["ann@contoso.example"], _settings.AiDrafting.ExcludeSenders);
        Assert.Single(_saves);
        Assert.Equal(1, _changed);
        var ev = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Ai, ev.Kind);
        Assert.Equal("AI drafts: added an entry to \"Never draft for\" (1 entry).", ev.Summary);
        Assert.Null(ev.Mailbox);
        Assert.False(_settings.AiDrafting.Enabled);
    }

    [Fact]
    public async Task Already_covered()
    {
        _settings.AiDrafting.ExcludeSenders = ["contoso.example"];
        Assert.Equal("ann@contoso.example is already on \"Never draft for\".", (await Prep()).Balloon);
    }

    [Fact]
    public async Task Full()
    {
        Fill(200);
        Assert.Equal(DontDraftTexts.Full, (await Prep()).Balloon);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Domain_happy_path()
    {
        Sender("ann@Mail.Contoso.Example");
        var p = await Prep(DontDraftKind.Domain);
        Assert.Equal("mail.contoso.example", p.Entry);
        Assert.True(p.NeedsConfirm);
        Assert.Equal("I won't draft replies to anyone at mail.contoso.example any more. You can undo this in Settings → AI drafts.", _svc.Apply(p));
        Assert.Equal(["mail.contoso.example"], _settings.AiDrafting.ExcludeSenders);
    }

    [Theory]
    [InlineData("me@example.org", "bob@example.org")]
    [InlineData("me@example.org", "bob@lab.example.org")]
    [InlineData("me@cs.example.edu", "x@example.edu")]
    public async Task Domain_own(string me, string sender)
    {
        _mb.Identity = new AiIdentity("Me", me);
        Sender(sender);
        Assert.Equal(DontDraftTexts.OwnDomain, (await Prep(DontDraftKind.Domain)).Balloon);
    }

    [Fact]
    public async Task Domain_covered_and_address_entry_does_not_cover()
    {
        Sender("ann@mail.contoso.example");
        _settings.AiDrafting.ExcludeSenders = ["contoso.example"];
        Assert.Equal("mail.contoso.example is already on \"Never draft for\".", (await Prep(DontDraftKind.Domain)).Balloon);
        Sender("bob@contoso.example");
        _settings.AiDrafting.ExcludeSenders = ["ann@contoso.example"];
        Assert.Equal("contoso.example", (await Prep(DontDraftKind.Domain)).Entry);
    }

    [Fact]
    public async Task Apply_rechecks_limit_and_covered()
    {
        var p = await Prep();
        Fill(200);
        Assert.Equal(DontDraftTexts.Full, _svc.Apply(p));
        _settings.AiDrafting.ExcludeSenders = ["ann@contoso.example"];
        Assert.Equal("ann@contoso.example is already on \"Never draft for\".", _svc.Apply(p));
        Assert.Empty(_saves);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public async Task Validation_failure()
    {
        var p = await Prep();
        _settings.AiDrafting.ExcludeSenders = ["not a domain!"];
        Assert.Equal("Must be an email address or a domain like example.org.", _svc.Apply(p));
        Assert.Empty(_saves);
        Assert.Equal(["not a domain!"], _settings.AiDrafting.ExcludeSenders);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public async Task Save_failure()
    {
        var p = await Prep();
        _saveThrows = new IOException("disk full");
        Assert.Equal("Couldn't change AI drafts: disk full", _svc.Apply(p));
        Assert.Empty(_settings.AiDrafting.ExcludeSenders);
        Assert.Empty(_hub.GetEvents());
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task Unexpected_exception_logs_type_only()
    {
        _mb.ReadThrow = new InvalidOperationException("ann@contoso.example");
        Assert.Equal("Couldn't change AI drafts: something went wrong (InvalidOperationException)", (await Prep()).Balloon);
        Assert.Contains(_log.Lines, l => l.Contains("InvalidOperationException"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("ann@"));
    }

    [Fact]
    public async Task Privacy_events_and_log()
    {
        _svc.Apply(await Prep());
        Sender("bob@Mail.Other.Example");
        _svc.Apply(await Prep(DontDraftKind.Domain));
        var events = _hub.GetEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(DontDraftTexts.AddedEvent(1), events[0].Summary);
        Assert.Equal(DontDraftTexts.AddedEvent(2), events[1].Summary);
        Assert.NotEmpty(_log.Lines);
        foreach (var l in _log.Lines)
        {
            Assert.DoesNotContain("ann", l);
            Assert.DoesNotContain("contoso", l);
            Assert.DoesNotContain("other", l);
            Assert.DoesNotContain("@", l);
        }
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _svc.PrepareAsync(DontDraftKind.Sender, cts.Token));
    }

    [Fact]
    public void Apply_argument_checks()
    {
        Assert.Throws<ArgumentNullException>(() => _svc.Apply(null!));
        Assert.Throws<ArgumentException>(() => _svc.Apply(new DontDraftPrepared(DontDraftKind.Sender, null, "x")));
    }
}

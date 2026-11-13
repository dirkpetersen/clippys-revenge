using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public sealed class ReplyDraftToolTests : IDisposable
{
    private sealed class FakeWriter : IReplyDraftWriter
    {
        public List<ReplyPlan> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public string Mailbox { get; set; } = "";

        public Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
        {
            Calls.Add(plan);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new RepliedDraft("R1", "Re:: Budget", plan.Mailbox ?? Mailbox));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m74-" + Guid.NewGuid());
    private readonly string _sigs;
    private readonly Settings _settings;
    private readonly FakeWriter _writer = new();
    private readonly ActivityHub _hub = new();
    private readonly Dictionary<SignatureKind, string?> _names = [];
    private readonly ReplyDraftTool _tool;

    public ReplyDraftToolTests()
    {
        _sigs = Path.Combine(_root, "sigs");
        Directory.CreateDirectory(_sigs);
        _settings = Settings.CreateDefault(Path.Combine(_root, "profile"));
        _tool = new ReplyDraftTool(_settings, new Signatures(k => _names.GetValueOrDefault(k), _sigs), _writer, _hub);
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Happy_default_mailbox()
    {
        var r = await _tool.RunAsync("ID1", "Thanks & ok", false, false, null, CancellationToken.None);
        Assert.Equal(new CreateDraftResult("R1", "Re:: Budget", "", false), r);
        var plan = Assert.Single(_writer.Calls);
        Assert.Equal("ID1", plan.Id);
        Assert.Null(plan.Mailbox);
        Assert.False(plan.ReplyAll);
        Assert.Equal("<p>Thanks &amp; ok</p>", plan.HtmlFragment);
        var ev = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Draft, ev.Kind);
        Assert.Equal("reply draft created: Re:: Budget", ev.Summary);
        Assert.Null(ev.Mailbox);
    }

    [Fact]
    public async Task Plan_carries_the_tool_marker_and_interactive_priority()
    {
        var plan = _tool.Plan("ID1", "x", false, false, null);
        Assert.Equal(DraftMarker.Tool, plan.Marker);
        Assert.Equal(WorkPriority.Interactive, plan.Priority);
        await _tool.RunAsync("ID1", "x", false, false, null, CancellationToken.None);
        var sent = Assert.Single(_writer.Calls);
        Assert.Equal(DraftMarker.Tool, sent.Marker);
        Assert.Equal(WorkPriority.Interactive, sent.Priority);
    }

    [Fact]
    public async Task Html_is_inserted_as_is_and_reply_all_flows()
    {
        await _tool.RunAsync(" ID1 ", "<b>x</b>", true, true, null, CancellationToken.None);
        var plan = Assert.Single(_writer.Calls);
        Assert.Equal("ID1", plan.Id);
        Assert.True(plan.ReplyAll);
        Assert.Equal("<b>x</b>", plan.HtmlFragment);
    }

    [Fact]
    public async Task Reply_signature_is_used_not_new()
    {
        _names[SignatureKind.New] = "NewSig";
        _names[SignatureKind.Reply] = "ReplySig";
        File.WriteAllText(Path.Combine(_sigs, "NewSig.htm"), "<body>NEWSIG</body>");
        File.WriteAllText(Path.Combine(_sigs, "ReplySig.htm"), "<html><body><p>REPLYSIG</p></body></html>");
        var r = await _tool.RunAsync("ID1", "Hi", false, false, null, CancellationToken.None);
        Assert.True(r.SignatureAdded);
        var frag = Assert.Single(_writer.Calls).HtmlFragment;
        Assert.Contains("REPLYSIG", frag);
        Assert.DoesNotContain("NEWSIG", frag);
        Assert.StartsWith("<p>Hi</p>", frag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_id(string? id)
    {
        Assert.Contains("\"id\"", Assert.Throws<McpException>(() => _tool.Plan(id, "x", false, false, null)).Message);
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task Shared_mailbox_enabled()
    {
        _settings.SharedMailboxes.Add(new SharedMailbox { Smtp = "Team@X.edu", Enabled = true });
        var r = await _tool.RunAsync("ID1", "x", false, false, " TEAM@x.edu ", CancellationToken.None);
        Assert.Equal("team@x.edu", r.Mailbox);
        Assert.Equal("team@x.edu", Assert.Single(_writer.Calls).Mailbox);
        Assert.Equal("team@x.edu", Assert.Single(_hub.GetEvents()).Mailbox);
    }

    [Fact]
    public void Shared_mailbox_unknown_or_disabled_or_all()
    {
        _settings.SharedMailboxes.Add(new SharedMailbox { Smtp = "off@x.edu", Enabled = false });
        Assert.Throws<McpException>(() => _tool.Plan("ID1", "x", false, false, "nope@x.edu"));
        Assert.Throws<McpException>(() => _tool.Plan("ID1", "x", false, false, "off@x.edu"));
        Assert.Throws<McpException>(() => _tool.Plan("ID1", "x", false, false, "all"));
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task Writer_failure_propagates_without_event()
    {
        _writer.Throw = new ItemNotFoundException("ID1");
        await Assert.ThrowsAsync<ItemNotFoundException>(() => _tool.RunAsync("ID1", "x", false, false, null, CancellationToken.None));
        Assert.Empty(_hub.GetEvents());
    }
}

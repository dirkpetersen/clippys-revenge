using System.Text;
using System.Text.Json;
using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public sealed class CreateDraftToolTests : IDisposable
{
    private sealed class FakeDraftWriter : IDraftWriter
    {
        public List<(DraftPlan Plan, CancellationToken Ct)> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public Func<DraftPlan, string>? SubjectOverride { get; set; }
        public bool MarkerSet { get; set; } = true;

        public Task<CreatedDraft> CreateAsync(DraftPlan plan, CancellationToken ct)
        {
            Calls.Add((plan, ct));
            if (Throw is not null) throw Throw;
            return Task.FromResult(new CreatedDraft("ENTRY1", SubjectOverride?.Invoke(plan) ?? plan.Subject, MarkerSet));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m73-" + Guid.NewGuid());
    private readonly string _att, _docs, _outside, _sigs;
    private readonly Settings _settings;
    private readonly FakeDraftWriter _writer = new();
    private readonly ActivityHub _hub = new();
    private readonly Dictionary<SignatureKind, string?> _names = [];
    private readonly CreateDraftTool _tool;

    public CreateDraftToolTests()
    {
        _att = Path.Combine(_root, "att");
        _docs = Path.Combine(_root, "docs");
        _outside = Path.Combine(_root, "outside");
        _sigs = Path.Combine(_root, "sigs");
        foreach (var d in new[] { _att, _docs, _outside, _sigs }) Directory.CreateDirectory(d);
        _settings = Settings.CreateDefault(Path.Combine(_root, "profile"));
        _settings.AttachmentDir = _att;
        _settings.AllowedFolders = [_docs];
        _tool = new CreateDraftTool(_settings,
            new Signatures(k => _names.GetValueOrDefault(k), _sigs), _writer, _hub);
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static CreateDraftArgs Args(string? to = "a@x.edu", string? body = "Hi", IReadOnlyList<string?>? att = null,
        string? mailbox = null, string? cc = null, string? bcc = null, string? subject = "S", bool html = false) =>
        new(to, subject, body, cc, bcc, html, att, mailbox);

    private string File_(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    private string Msg(Func<Task> a) => Assert.ThrowsAsync<McpException>(a).GetAwaiter().GetResult().Message;

    [Fact]
    public async Task Happy_own()
    {
        var r = await _tool.RunAsync(Args("Sample, Bob <bob@x.edu>; b@x.edu", subject: "Budget"), CancellationToken.None);
        Assert.Equal(new CreateDraftResult("ENTRY1", "Budget", "", false), r);
        var plan = Assert.Single(_writer.Calls).Plan;
        Assert.Equal([new Recipient("Sample, Bob", "bob@x.edu"), new Recipient("", "b@x.edu")], plan.To);
        Assert.Empty(plan.Cc);
        Assert.Empty(plan.Bcc);
        Assert.Equal("<p>Hi</p>", plan.HtmlFragment);
        Assert.Empty(plan.AttachmentPaths);
        Assert.Equal("", plan.Mailbox);
        var ev = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Draft, ev.Kind);
        Assert.Equal("draft created: Budget", ev.Summary);
        Assert.Null(ev.Mailbox);
    }

    [Fact]
    public async Task Plan_carries_the_tool_marker()
    {
        Assert.Equal(DraftMarker.Tool, _tool.Plan(Args("b@x.edu")).Marker);
        await _tool.RunAsync(Args("b@x.edu"), CancellationToken.None);
        Assert.Equal(DraftMarker.Tool, Assert.Single(_writer.Calls).Plan.Marker);
    }

    [Fact]
    public async Task Marker_not_set_leaves_the_result_unchanged()
    {
        var ok = await _tool.RunAsync(Args("b@x.edu", subject: "S"), CancellationToken.None);
        _writer.MarkerSet = false;
        var failed = await _tool.RunAsync(Args("b@x.edu", subject: "S"), CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(ok), JsonSerializer.Serialize(failed));
    }

    [Fact]
    public void Json_shape()
    {
        var json = JsonSerializer.Serialize(new CreateDraftResult("i", "s", "", true));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["id", "subject", "mailbox", "signature_added"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Cc_bcc()
    {
        var plan = _tool.Plan(Args(cc: "c@x.edu", bcc: "  "));
        Assert.Single(plan.Cc);
        Assert.Empty(plan.Bcc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ")]
    public async Task No_to(string? to)
    {
        var m = Msg(() => _tool.RunAsync(Args(to), CancellationToken.None));
        Assert.Equal("create_draft needs at least one recipient in \"to\", like \"Sample, Bob <bob.sample@example.org>; someone@example.org\".", m);
        Assert.Empty(_writer.Calls);
        Assert.Empty(_hub.GetEvents());
        await Task.CompletedTask;
    }

    [Fact]
    public void Bad_recipients()
    {
        Assert.Equal("\"to\": Recipient \"bob\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".",
            Assert.Throws<McpException>(() => _tool.Plan(Args("bob"))).Message);
        Assert.StartsWith("\"cc\": ", Assert.Throws<McpException>(() => _tool.Plan(Args(cc: "bob"))).Message);
        Assert.StartsWith("\"bcc\": ", Assert.Throws<McpException>(() => _tool.Plan(Args(bcc: "bob"))).Message);
    }

    [Fact]
    public void Attachment_ok_and_relative()
    {
        var a = File_(_docs, "a.pdf");
        File_(_att, "report.pdf");
        var plan = _tool.Plan(Args(att: [a, "report.pdf"]));
        Assert.Equal([Path.GetFullPath(a), Path.Combine(_att, "report.pdf")], plan.AttachmentPaths);
    }

    [Fact]
    public async Task Attachments_checked_first()
    {
        var a = File_(_docs, "a.pdf");
        var b = File_(_outside, "b.pdf");
        var m = Msg(() => _tool.RunAsync(Args(att: [a, b]), CancellationToken.None));
        Assert.Contains("is not allowed", m);
        Assert.Contains(_docs, m);
        Assert.EndsWith("Nothing was created.", m);
        Assert.Empty(_writer.Calls);
        var ev = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Blocked, ev.Kind);
        Assert.Equal("create_draft: attachment 2 refused (it is outside the allowed folders)", ev.Summary);
        Assert.DoesNotContain(b, ev.Summary);
        await Task.CompletedTask;
    }

    [Fact]
    public void Traversal()
    {
        File_(_root, "secret.txt");
        var m = Assert.Throws<McpException>(() => _tool.Plan(Args(att: [Path.Combine(_docs, "..", "secret.txt")]))).Message;
        Assert.EndsWith("Nothing was created.", m);
        Assert.Equal(ActivityKind.Blocked, Assert.Single(_hub.GetEvents()).Kind);
    }

    [Fact]
    public void Missing_file()
    {
        var a = File_(_docs, "a.pdf");
        var m = Assert.Throws<McpException>(() => _tool.Plan(Args(att: [a, Path.Combine(_docs, "none.pdf")]))).Message;
        Assert.StartsWith("Attachment 2: File not found:", m);
        Assert.EndsWith("Nothing was created.", m);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public void Directory_as_attachment()
    {
        var d = Path.Combine(_docs, "sub");
        Directory.CreateDirectory(d);
        var m = Assert.Throws<McpException>(() => _tool.Plan(Args(att: [d]))).Message;
        Assert.StartsWith("Attachment 1: ", m);
        Assert.Contains("is a folder, not a file.", m);
    }

    [Fact]
    public void Blank_entry()
    {
        var expected = $"Attachment 1 is empty. Pass the full path of an existing file in the allowed folders: {string.Join("; ", _settings.EffectiveAllowedFolders())}.";
        Assert.Equal(expected, Assert.Throws<McpException>(() => _tool.Plan(Args(att: [null]))).Message);
        Assert.Equal(expected, Assert.Throws<McpException>(() => _tool.Plan(Args(att: ["  "]))).Message);
    }

    [Fact]
    public void Empty_allowed_folders_still_allows_attachment_dir()
    {
        _settings.AllowedFolders = [];
        var a = File_(_att, "ok.txt");
        Assert.Single(_tool.Plan(Args(att: [a])).AttachmentPaths);
        var d = File_(_docs, "no.txt");
        Assert.Throws<McpException>(() => _tool.Plan(Args(att: [d])));
        Assert.Equal(ActivityKind.Blocked, Assert.Single(_hub.GetEvents()).Kind);
    }

    [Fact]
    [Trait("os", "windows")]
    public void Junction_escape()
    {
        if (!OperatingSystem.IsWindows()) return;
        File_(_outside, "j.txt");
        var link = Path.Combine(_docs, "link");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{_outside}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();
        Assert.Throws<McpException>(() => _tool.Plan(Args(att: [Path.Combine(link, "j.txt")])));
        Assert.Equal(ActivityKind.Blocked, Assert.Single(_hub.GetEvents()).Kind);
        Directory.Delete(link);
    }

    private void AddShared(string smtp, bool enabled = true) =>
        _settings.SharedMailboxes.Add(new SharedMailbox { Smtp = smtp, Enabled = enabled });

    [Fact]
    public async Task Shared_enabled()
    {
        AddShared("Shared@X.edu");
        var r = await _tool.RunAsync(Args(mailbox: " SHARED@x.edu "), CancellationToken.None);
        Assert.Equal("shared@x.edu", r.Mailbox);
        Assert.Equal("shared@x.edu", Assert.Single(_hub.GetEvents()).Mailbox);

        var b = File_(_outside, "b.pdf");
        Assert.Throws<McpException>(() => _tool.Plan(Args(mailbox: "shared@x.edu", att: [b])));
        Assert.Equal("shared@x.edu", _hub.GetEvents().Last(e => e.Kind == ActivityKind.Blocked).Mailbox);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_unknown_or_disabled(bool present)
    {
        AddShared("other@x.edu");
        if (present) AddShared("off@x.edu", enabled: false);
        var m = Assert.Throws<McpException>(() => _tool.Plan(Args(mailbox: " off@x.edu "))).Message;
        Assert.Equal("Mailbox \"off@x.edu\" is not configured or not enabled. Available mailboxes: \"\" (your own mailbox), \"other@x.edu\". To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.", m);
        Assert.Empty(_writer.Calls);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public void All_refused(string all) =>
        Assert.Equal("Mailbox \"all\" only works with search tools. Use your own mailbox (leave mailbox empty) or one shared mailbox address.",
            Assert.Throws<McpException>(() => _tool.Plan(Args(mailbox: all))).Message);

    [Fact]
    public void Check_order()
    {
        var b = File_(_outside, "b.pdf");
        var m = Assert.Throws<McpException>(() => _tool.Plan(Args(mailbox: "nope@x.edu", att: [b]))).Message;
        Assert.Contains("is not configured or not enabled", m);
        var m2 = Assert.Throws<McpException>(() => _tool.Plan(Args(to: "bob", att: [b]))).Message;
        Assert.StartsWith("\"to\": ", m2);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public void Signature()
    {
        _names[SignatureKind.New] = "Work";
        _names[SignatureKind.Reply] = "Other";
        File.WriteAllText(Path.Combine(_sigs, "Other.htm"), "<body>no</body>");
        Assert.False(_tool.Plan(Args()).SignatureAdded);
        File.WriteAllText(Path.Combine(_sigs, "Work.htm"), "<html><body><p>Sig</p></body></html>");
        var plan = _tool.Plan(Args());
        Assert.Equal("<p>Hi</p><br><p>Sig</p>", plan.HtmlFragment);
        Assert.True(plan.SignatureAdded);
    }

    [Fact]
    public void Html_subject_body_null()
    {
        Assert.Equal("<b>x</b>", _tool.Plan(Args(body: "<b>x</b>", html: true)).HtmlFragment);
        Assert.Equal("", _tool.Plan(Args(subject: null)).Subject);
        Assert.Equal("a b c", _tool.Plan(Args(subject: " a\r\nb\nc ")).Subject);
        Assert.Equal("", _tool.Plan(Args(body: null)).HtmlFragment);
    }

    [Fact]
    public async Task Writer_failure_propagates()
    {
        var ex = new McpException("Outlook is not running.");
        _writer.Throw = ex;
        var got = await Assert.ThrowsAsync<McpException>(() => _tool.RunAsync(Args(), CancellationToken.None));
        Assert.Same(ex, got);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public async Task Cancellation_token_passed()
    {
        using var cts = new CancellationTokenSource();
        await _tool.RunAsync(Args(), cts.Token);
        Assert.Equal(cts.Token, _writer.Calls[0].Ct);
    }

    [Fact]
    public async Task Event_subject_shortened()
    {
        _writer.SubjectOverride = _ => new string('s', 100);
        var r = await _tool.RunAsync(Args(), CancellationToken.None);
        Assert.Equal(100, r.Subject.Length);
        Assert.Equal("draft created: " + new string('s', 80) + "…", _hub.GetEvents()[^1].Summary);
        _writer.SubjectOverride = _ => "";
        await _tool.RunAsync(Args(), CancellationToken.None);
        Assert.Equal("draft created: (no subject)", _hub.GetEvents()[^1].Summary);
    }

    [Fact]
    public void Ctor_nulls()
    {
        var sig = new Signatures(_ => null, _sigs);
        Assert.Throws<ArgumentNullException>(() => new CreateDraftTool(null!, sig, _writer, _hub));
        Assert.Throws<ArgumentNullException>(() => new CreateDraftTool(_settings, null!, _writer, _hub));
        Assert.Throws<ArgumentNullException>(() => new CreateDraftTool(_settings, sig, null!, _hub));
        Assert.Throws<ArgumentNullException>(() => new CreateDraftTool(_settings, sig, _writer, null!));
    }

    [Fact]
    public async Task Events_do_not_carry_body()
    {
        await _tool.RunAsync(Args(body: "SECRET-BODY"), CancellationToken.None);
        Assert.DoesNotContain(_hub.GetEvents(), e => e.Summary.Contains("SECRET-BODY"));
    }

    [Fact]
    public void Source_has_no_send_or_display()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ClippysRevenge.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var files = new[]
        {
            "ClippysRevenge.Core/Signatures.cs", "ClippysRevenge.Core/DraftBody.cs", "ClippysRevenge.Core/CreateDraft.cs",
            "ClippysRevenge.App/OutlookMail.Drafts.cs", "ClippysRevenge.App/OutlookDraftWriter.cs",
            "ClippysRevenge.App/McpTools.Drafts.cs", "ClippysRevenge.App/SignatureRegistry.cs",
        };
        foreach (var f in files)
        {
            var text = File.ReadAllText(Path.Combine(dir, "src", f), Encoding.UTF8);
            Assert.DoesNotContain(".Send(", text);
            Assert.DoesNotContain(".Display(", text);
        }
    }
}

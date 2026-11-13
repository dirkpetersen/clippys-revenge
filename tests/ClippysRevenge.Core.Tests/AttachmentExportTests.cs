using System.Text.Json;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AttachmentExportTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeSaver : IAttachmentSaver
    {
        public List<(string Id, IReadOnlyList<AttachmentInfo> Wanted, string Dir)> Calls { get; } = [];
        public Dictionary<string, Exception> Throws { get; } = [];
        public HashSet<int> FailPositions { get; } = [];
        public List<AttachmentInfo> Live { get; set; } = [new(1, "a.pdf", 1, false), new(2, "b.pdf", 1, false)];

        private SavedAttachment Write(string dir, string name)
        {
            var p = FileNames.UniquePath(dir, name);
            File.WriteAllText(p, "x");
            return new SavedAttachment(p, name, 1);
        }

        public Task<SavedAttachment> SaveOneAsync(string id, string? filename, int? position, string dir, CancellationToken ct)
        {
            Calls.Add((id, [], dir));
            if (Throws.TryGetValue(id, out var ex)) throw ex;
            var t = AttachmentSelection.SelectOne(Live, filename, position);
            return Task.FromResult(Write(dir, t.FileName));
        }

        public Task<IReadOnlyList<AttachmentSaveOutcome>> SaveManyAsync(
            string id, IReadOnlyList<AttachmentInfo> wanted, string dir, CancellationToken ct)
        {
            Calls.Add((id, wanted, dir));
            if (Throws.TryGetValue(id, out var ex)) throw ex;
            IReadOnlyList<AttachmentSaveOutcome> r = wanted
                .Select(w => FailPositions.Contains(w.Position)
                    ? new AttachmentSaveOutcome(w, null, "disk full")
                    : new AttachmentSaveOutcome(w, Write(dir, w.FileName), null))
                .ToList();
            return Task.FromResult(r);
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m72-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ActivityHub _hub = new();
    private readonly FakeSaver _saver = new();
    private MailboxIndexes? _mi;

    public AttachmentExportTests()
    {
        Directory.CreateDirectory(_root);
        _settings = Settings.CreateDefault(Path.Combine(_root, "profile"));
        _settings.AttachmentDir = Path.Combine(_root, "att");
        _settings.AllowedFolders = [Path.Combine(_root, "att"), Path.Combine(_root, "docs")];
    }

    public void Dispose()
    {
        _mi?.Dispose();
        _hub.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private MailboxIndexes Indexes(params string[] shared) =>
        _mi ??= new MailboxIndexes(Path.Combine(_root, "data"), shared);

    private AttachmentExport Make(MailboxIndexes? mi = null) =>
        new(_saver, mi ?? Indexes(), () => _settings, _hub, TimeZoneInfo.Utc);

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private static IndexedEmail Mail(string id, int day, params AttachmentInfo[] atts) => new()
    {
        EntryId = id, Folder = "Inbox", Subject = "s", EventTime = Base.AddDays(day), Body = "b", Attachments = atts,
    };

    private static AttachmentInfo At(int p, string n, bool inline = false) => new(p, n, 1, inline);

    private int Blocked => _hub.GetEvents().Count(e => e.Kind == ActivityKind.Blocked);

    // ---- ResolveSaveDir

    [Fact]
    public void E1_Default()
    {
        var dir = Make().ResolveSaveDir(null, "get_attachment");
        Assert.Equal(Path.GetFullPath(P("att")), dir);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public void E2_Relative()
    {
        var dir = Make().ResolveSaveDir("invoices/2026", "get_attachment");
        Assert.Equal(Path.GetFullPath(P("att", "invoices", "2026")), dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void E3_SecondRootTrimmed()
    {
        var dir = Make().ResolveSaveDir("  " + P("docs", "x") + "  ", "get_attachment");
        Assert.Equal(Path.GetFullPath(P("docs", "x")), dir);
    }

    [Fact]
    public void E4_Refused()
    {
        var bad = P("elsewhere");
        var ex = Assert.Throws<McpException>(() => Make().ResolveSaveDir(bad, "save_attachments"));
        Assert.Contains("Allowed folders:", ex.Message);
        Assert.Contains(P("att"), ex.Message);
        Assert.Contains(P("docs"), ex.Message);
        var ev = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Blocked, ev.Kind);
        Assert.Equal($"save_attachments refused save_dir \"{bad}\": it is outside the allowed folders", ev.Summary);
    }

    [Fact]
    public void E5_Escape()
    {
        Assert.Throws<McpException>(() => Make().ResolveSaveDir("../escape", "get_attachment"));
        Assert.Equal(1, Blocked);
    }

    [Fact]
    public void E6_PathIsFile_NoBlockedEvent()
    {
        Directory.CreateDirectory(P("att"));
        var f = P("att", "file.txt");
        File.WriteAllText(f, "x");
        var ex = Assert.Throws<McpException>(() => Make().ResolveSaveDir(f, "get_attachment"));
        Assert.StartsWith("Could not use save_dir \"", ex.Message);
        Assert.Equal(0, Blocked);
    }

    [Fact]
    public void E7_LongPathTruncated()
    {
        var bad = P("elsewhere") + "/" + new string('a', 300);
        Assert.Throws<McpException>(() => Make().ResolveSaveDir(bad, "get_attachment"));
        var s = Assert.Single(_hub.GetEvents()).Summary;
        var start = s.IndexOf('"') + 1;
        var end = s.IndexOf("\":", start, StringComparison.Ordinal);
        Assert.Equal(200, end - start);
    }

    [Fact]
    public void E8_LiveSettings()
    {
        var export = Make();
        Assert.Throws<McpException>(() => export.ResolveSaveDir(P("new"), "get_attachment"));
        _settings.AllowedFolders = [.. _settings.AllowedFolders, P("new")];
        Assert.Equal(Path.GetFullPath(P("new")), export.ResolveSaveDir(P("new"), "get_attachment"));
    }

    // ---- GetAsync

    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task E9_IdRequired(string? id)
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => Make().GetAsync(id, null, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("id is required. Use the id from search_emails.", ex.Message);
        Assert.Empty(_saver.Calls);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public async Task E10_RefusedBeforeSaver()
    {
        await Assert.ThrowsAsync<McpException>(() => Make().GetAsync("E1", null, 2, P("elsewhere"), TestContext.Current.CancellationToken));
        Assert.Equal(1, Blocked);
        Assert.Empty(_saver.Calls);
    }

    [Fact]
    public async Task E11_E12_Get()
    {
        var export = Make();
        var r = await export.GetAsync(" E1 ", "a.pdf", null, null, TestContext.Current.CancellationToken);
        Assert.Equal("E1", _saver.Calls[0].Id);
        Assert.Equal(Path.GetFullPath(P("att")), _saver.Calls[0].Dir);
        Assert.Equal(Path.Combine(Path.GetFullPath(P("att")), "a.pdf"), r.Path);
        Assert.Equal("a.pdf", r.FileName);
        Assert.Equal(1, r.Size);
        var r2 = await export.GetAsync("E1", "a.pdf", null, null, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(Path.GetFullPath(P("att")), "a (2).pdf"), r2.Path);
    }

    [Fact]
    public async Task E13_SaverErrorPropagates()
    {
        _saver.Throws["E1"] = new McpException("boom");
        var ex = await Assert.ThrowsAsync<McpException>(() => Make().GetAsync("E1", null, 1, null, TestContext.Current.CancellationToken));
        Assert.Equal("boom", ex.Message);
    }

    // ---- SaveAsync

    private void Seed()
    {
        Indexes().For("").UpsertBatch([
            Mail("A", 3, At(1, "r.pdf"), At(2, "logo.png", true)),
            Mail("B", 2, At(1, "r.pdf")),
            Mail("C", 1),
        ]);
    }

    [Fact]
    public async Task E14_Bulk()
    {
        Seed();
        var r = await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { HasAttachment = true } }, TestContext.Current.CancellationToken);
        Assert.Equal(["A", "B"], _saver.Calls.Select(c => c.Id).ToArray());
        Assert.Equal(2, r.Count);
        Assert.Empty(r.Failed);
        Assert.Null(r.Note);
        Assert.Equal([("A", 1, "r.pdf"), ("B", 1, "r.pdf")], r.Saved.Select(s => (s.Id, s.Position, s.FileName)).ToArray());
        Assert.All(r.Saved, s => Assert.Equal("", s.Mailbox));
        Assert.EndsWith("r.pdf", r.Saved[0].Path);
        Assert.EndsWith("r (2).pdf", r.Saved[1].Path);
    }

    [Fact]
    public async Task E15_IncludeInline()
    {
        Seed();
        var r = await Make().SaveAsync(
            new SaveAttachmentsArgs { IncludeInline = true, Search = new() { HasAttachment = true } }, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], _saver.Calls[0].Wanted.Select(w => w.Position).ToArray());
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public async Task E16_Ext()
    {
        Indexes().For("").UpsertBatch([Mail("A", 1, At(1, "a.pdf"), At(2, "b.docx"))]);
        await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { AttachmentExt = "pdf" } }, TestContext.Current.CancellationToken);
        Assert.Equal([1], _saver.Calls[0].Wanted.Select(w => w.Position).ToArray());
    }

    [Fact]
    public async Task E17_NoMatch()
    {
        Seed();
        var r = await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { Query = "zzzz" } }, TestContext.Current.CancellationToken);
        Assert.Empty(r.Saved);
        Assert.Empty(r.Failed);
        Assert.Equal(0, r.Count);
        Assert.Equal("No emails matched the filters.", r.Note);
        Assert.Empty(_saver.Calls);
    }

    [Fact]
    public async Task E18_OnlyInline()
    {
        Indexes().For("").UpsertBatch([Mail("A", 1, At(1, "logo.png", true))]);
        var r = await Make().SaveAsync(new SaveAttachmentsArgs(), TestContext.Current.CancellationToken);
        Assert.Equal("1 email(s) matched, but none had attachments to save. Inline attachments are skipped unless include_inline is true.", r.Note);
        Assert.Empty(_saver.Calls);
    }

    private void SeedThree() => Indexes().For("").UpsertBatch([
        Mail("A", 3, At(1, "a.pdf")), Mail("B", 2, At(1, "b.pdf"), At(2, "b2.pdf")), Mail("D", 1, At(1, "d.pdf"))]);

    [Fact]
    public async Task E19_EmailLevelFailureContinues()
    {
        SeedThree();
        _saver.Throws["B"] = new McpException("gone item");
        var r = await Make().SaveAsync(new SaveAttachmentsArgs(), TestContext.Current.CancellationToken);
        Assert.Equal(3, _saver.Calls.Count);
        Assert.Equal(["A", "D"], r.Saved.Select(s => s.Id).ToArray());
        Assert.Equal(2, r.Failed.Count);
        Assert.All(r.Failed, f => { Assert.Equal("B", f.Id); Assert.Equal("gone item", f.Error); });
    }

    [Fact]
    public async Task E20_UnavailableStops()
    {
        SeedThree();
        _saver.Throws["A"] = new OutlookUnavailableException();
        var r = await Make().SaveAsync(new SaveAttachmentsArgs(), TestContext.Current.CancellationToken);
        Assert.Single(_saver.Calls);
        Assert.Equal(4, r.Failed.Count);
        Assert.All(r.Failed, f => Assert.Equal(OutlookUnavailableException.DefaultMessage, f.Error));
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public async Task E21_OutcomeError()
    {
        Indexes().For("").UpsertBatch([Mail("A", 1, At(1, "a.pdf"), At(2, "b.pdf"))]);
        _saver.FailPositions.Add(2);
        var r = await Make().SaveAsync(new SaveAttachmentsArgs(), TestContext.Current.CancellationToken);
        Assert.Single(r.Saved);
        var f = Assert.Single(r.Failed);
        Assert.Equal((2, "b.pdf", "disk full"), (f.Position, f.FileName, f.Error));
    }

    [Fact]
    public async Task E22_CancelPropagates()
    {
        SeedThree();
        _saver.Throws["A"] = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make().SaveAsync(new SaveAttachmentsArgs(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task E23_BadDate()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { DateFrom = "03/01/2026" } }, TestContext.Current.CancellationToken));
        Assert.StartsWith("date_from \"03/01/2026\" is not a valid date.", ex.Message);
        Assert.Empty(_saver.Calls);
    }

    [Fact]
    public async Task E24_UnknownMailbox()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { Mailbox = "nobody@x.org" } }, TestContext.Current.CancellationToken));
        Assert.StartsWith("Mailbox \"nobody@x.org\" is not configured or not enabled.", ex.Message);
    }

    [Fact]
    public async Task E25_AllMailboxes()
    {
        var mi = Indexes("s@x.org");
        mi.For("").UpsertBatch([Mail("A", 2, At(1, "a.pdf"))]);
        mi.For("s@x.org").UpsertBatch([Mail("S", 1, At(1, "s.pdf"))]);
        var r = await Make(mi).SaveAsync(new SaveAttachmentsArgs { Search = new() { Mailbox = "all" } }, TestContext.Current.CancellationToken);
        Assert.Equal([("A", ""), ("S", "s@x.org")], r.Saved.Select(s => (s.Id, s.Mailbox)).ToArray());
    }

    [Fact]
    public async Task E26_LimitOffsetCountEmails()
    {
        SeedThree();
        await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { Limit = 2 } }, TestContext.Current.CancellationToken);
        Assert.Equal(["A", "B"], _saver.Calls.Select(c => c.Id).ToArray());
        _saver.Calls.Clear();
        await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { Limit = 2, Offset = 2 } }, TestContext.Current.CancellationToken);
        Assert.Equal(["D"], _saver.Calls.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task E27_LimitClamped()
    {
        SeedThree();
        await Make().SaveAsync(new SaveAttachmentsArgs { Search = new() { Limit = 0 } }, TestContext.Current.CancellationToken);
        Assert.Single(_saver.Calls);
    }

    [Fact]
    public async Task E28_PathCheckedFirst()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => Make().SaveAsync(
            new SaveAttachmentsArgs { SaveDir = P("elsewhere"), Search = new() { Mailbox = "nobody@x.org" } }, TestContext.Current.CancellationToken));
        Assert.Contains("Allowed folders:", ex.Message);
        Assert.Equal(1, Blocked);
    }

    [Fact]
    public void E29_Json()
    {
        var r = new SaveAttachmentsResult(
            [new SavedAttachmentEntry("i", "m", 1, "f", "p", 2)],
            [new FailedAttachmentEntry("i", "m", 1, "f", "e")], 1, "n");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r));
        var root = doc.RootElement;
        Assert.Equal(["saved", "failed", "count", "note"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["id", "mailbox", "position", "filename", "path", "size"],
            root.GetProperty("saved")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["id", "mailbox", "position", "filename", "error"],
            root.GetProperty("failed")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void E30_CtorNulls()
    {
        var mi = Indexes();
        Assert.Throws<ArgumentNullException>(() => new AttachmentExport(null!, mi, () => _settings, _hub));
        Assert.Throws<ArgumentNullException>(() => new AttachmentExport(_saver, null!, () => _settings, _hub));
        Assert.Throws<ArgumentNullException>(() => new AttachmentExport(_saver, mi, null!, _hub));
        Assert.Throws<ArgumentNullException>(() => new AttachmentExport(_saver, mi, () => _settings, null!));
    }
}

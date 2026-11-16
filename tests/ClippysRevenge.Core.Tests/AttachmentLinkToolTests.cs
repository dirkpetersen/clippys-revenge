using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AttachmentLinkToolTests : IDisposable
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("T", TimeSpan.FromHours(-8), "T", "T");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-linktool-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly CaptureLogger _log = new();
    private readonly FakeSaver _saver = new();
    private int _port = 8765;
    private Action<string> _ensure = d => Directory.CreateDirectory(d);

    private AttachmentLinks NewLinks() => new(new AttachmentLinksOptions
    {
        Folder = Path.Combine(_root, "links"),
        EnsurePrivateFolder = d => _ensure(d),
        Time = _time,
    });

    private AttachmentLinkTool NewTool(AttachmentLinks links) => new(links, _saver, () => _port, Tz, _log);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private int FileCount(AttachmentLinks links) =>
        Directory.Exists(links.Folder) ? Directory.GetFiles(links.Folder).Length : 0;

    [Fact]
    public async Task HappyPath()
    {
        using var links = NewLinks();
        var tool = NewTool(links);
        _saver.Bytes = [1, 2, 3, 4, 5];
        _saver.Name = "report.pdf";

        var r = await tool.CreateAsync(" abc ", "report.pdf", null, CancellationToken.None);

        Assert.Matches(@"^http://127\.0\.0\.1:8765/files/[A-Za-z0-9_-]{43}$", r.Url);
        Assert.Equal("report.pdf", r.FileName);
        Assert.Equal(5, r.Size);
        Assert.Equal(LinkTexts.FormatExpires(ManualTimeProvider.T0 + TimeSpan.FromMinutes(10), Tz), r.ExpiresAt);
        Assert.Equal("curl -fsS -H \"Authorization: Bearer $CLIPPY_TOKEN\" -o 'report.pdf' '" + r.Url + "'", r.Curl);
        Assert.Equal(LinkTexts.Note, r.Note);
        Assert.Equal(1, links.OpenCount);

        var call = Assert.Single(_saver.Calls);
        Assert.Equal("abc", call.Id);
        Assert.Equal("report.pdf", call.Filename);
        Assert.Null(call.Position);
        Assert.Equal(AttachmentLinks.MaxLinkBytes, call.MaxBytes);
        Assert.Equal(links.Folder, Path.GetDirectoryName(call.TempPath));
        Assert.True(LinkPaths.IsTempFileName(Path.GetFileName(call.TempPath)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(call.TempPath));

        var token = r.Url[(r.Url.LastIndexOf('/') + 1)..];
        var link = links.TryConsume(token);
        Assert.NotNull(link);
        Assert.Equal(call.TempPath, link!.TempPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankId_Refused(string? id)
    {
        using var links = NewLinks();
        var ex = await Assert.ThrowsAsync<McpException>(() => NewTool(links).CreateAsync(id, null, null, CancellationToken.None));
        Assert.Equal("id is required. Use the id from search_emails.", ex.Message);
        Assert.Empty(_saver.Calls);
        Assert.Equal(0, FileCount(links));
    }

    [Fact]
    public async Task TooManyOpen_OnEleventh()
    {
        using var links = NewLinks();
        var tool = NewTool(links);
        for (var i = 0; i < 10; i++) await tool.CreateAsync("abc", null, null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AttachmentLinkException>(() => tool.CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Equal(LinkRefusal.TooManyOpen, ex.Reason);
        Assert.Equal(LinkTexts.TooManyOpen, ex.Message);
        Assert.Equal(10, _saver.Calls.Count);
    }

    [Fact]
    public async Task SaverThrows_FileDiscarded_SameException()
    {
        using var links = NewLinks();
        var boom = new McpException("gone");
        _saver.Throw = boom;
        _saver.WriteBeforeThrow = true;
        var ex = await Assert.ThrowsAsync<McpException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Same(boom, ex);
        Assert.False(File.Exists(Assert.Single(_saver.Calls).TempPath));
        Assert.Equal(0, links.OpenCount);
    }

    [Fact]
    public async Task SaverCancelled_FileDiscarded()
    {
        using var links = NewLinks();
        _saver.Throw = new OperationCanceledException();
        _saver.WriteBeforeThrow = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.False(File.Exists(Assert.Single(_saver.Calls).TempPath));
    }

    [Fact]
    public async Task SaverTooBig_Propagates()
    {
        using var links = NewLinks();
        _saver.Throw = new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(150L * 1024 * 1024));
        var ex = await Assert.ThrowsAsync<AttachmentLinkException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Equal("The attachment is 150 MB; attachment_link handles up to 100 MB. Use get_attachment to save it on the Windows side.", ex.Message);
        Assert.Equal(0, FileCount(links));
    }

    [Fact]
    public async Task BytesLimit_AtRegistration()
    {
        using var links = NewLinks();
        var tool = NewTool(links);
        _saver.ReportedSize = 90L * 1024 * 1024;
        _saver.Bytes = [1];
        await tool.CreateAsync("abc", null, null, CancellationToken.None);
        await tool.CreateAsync("abc", null, null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AttachmentLinkException>(() => tool.CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Equal(LinkRefusal.TooManyBytes, ex.Reason);
        Assert.Equal(LinkTexts.TooManyBytes, ex.Message);
        Assert.False(File.Exists(_saver.Calls[2].TempPath));
        Assert.Equal(2, links.OpenCount);
    }

    [Fact]
    public async Task FolderUnusable_SaverNotCalled()
    {
        using var links = NewLinks();
        _ensure = _ => throw new IOException("nope");
        var ex = await Assert.ThrowsAsync<AttachmentLinkException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Equal(LinkRefusal.FolderUnusable, ex.Reason);
        Assert.Equal("Clippy can't use its private download folder. Use get_attachment instead.", ex.Message);
        Assert.Empty(_saver.Calls);
    }

    [Fact]
    public async Task PortReadPerCall()
    {
        using var links = NewLinks();
        var tool = NewTool(links);
        var a = await tool.CreateAsync("abc", null, null, CancellationToken.None);
        Assert.StartsWith("http://127.0.0.1:8765/files/", a.Url);
        _port = 54321;
        var b = await tool.CreateAsync("abc", null, null, CancellationToken.None);
        Assert.StartsWith("http://127.0.0.1:54321/files/", b.Url);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task InvalidPort_DiscardsFile_ActionableError(int port)
    {
        using var links = NewLinks();
        _port = port;
        var ex = await Assert.ThrowsAsync<McpException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.Equal("Clippy's server isn't running, so it can't make a download link. Try again in a moment.", ex.Message);
        Assert.Equal(0, links.OpenCount);
        Assert.False(File.Exists(Assert.Single(_saver.Calls).TempPath));
    }

    [Fact]
    public async Task DisposedStore_AddDeletesFile()
    {
        var links = NewLinks();
        _saver.OnSave = links.Dispose;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => NewTool(links).CreateAsync("abc", null, null, CancellationToken.None));
        Assert.False(File.Exists(Assert.Single(_saver.Calls).TempPath));
    }

    [Fact]
    public async Task ShellUnsafeName()
    {
        using var links = NewLinks();
        _saver.Name = "-it's.pdf";
        var r = await NewTool(links).CreateAsync("abc", null, null, CancellationToken.None);
        Assert.Equal("-it's.pdf", r.FileName);
        Assert.Contains("-o '_it_s.pdf'", r.Curl);
    }

    [Fact]
    public async Task Logs_NeverContainSecrets()
    {
        using var links = NewLinks();
        var tool = NewTool(links);
        _saver.Name = "report.pdf";
        var r = await tool.CreateAsync("abc", "report.pdf", null, CancellationToken.None);
        for (var i = 0; i < 9; i++) await tool.CreateAsync("abc", null, null, CancellationToken.None);
        await Assert.ThrowsAsync<AttachmentLinkException>(() => tool.CreateAsync("abc", null, null, CancellationToken.None));

        var token = r.Url[(r.Url.LastIndexOf('/') + 1)..];
        var forbidden = new[] { token, r.Url, _saver.Calls[0].TempPath, links.Folder, "report.pdf", "abc" };
        Assert.NotEmpty(_log.Messages);
        foreach (var m in _log.Messages)
            foreach (var f in forbidden)
                Assert.DoesNotContain(f, m);
        Assert.Contains(_log.Messages, m => m.Contains("link created"));
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        using var links = NewLinks();
        Assert.Throws<ArgumentNullException>(() => new AttachmentLinkTool(null!, _saver, () => 1));
        Assert.Throws<ArgumentNullException>(() => new AttachmentLinkTool(links, null!, () => 1));
        Assert.Throws<ArgumentNullException>(() => new AttachmentLinkTool(links, _saver, null!));
    }

    private sealed record Call(string Id, string? Filename, int? Position, string TempPath, long MaxBytes);

    private sealed class FakeSaver : IAttachmentLinkSaver
    {
        public List<Call> Calls { get; } = [];
        public byte[] Bytes { get; set; } = [1, 2, 3];
        public string Name { get; set; } = "a.pdf";
        public long? ReportedSize { get; set; }
        public Exception? Throw { get; set; }
        public bool WriteBeforeThrow { get; set; }
        public Action? OnSave { get; set; }

        public Task<SavedAttachment> SaveForLinkAsync(string id, string? filename, int? position, string tempPath,
            long maxBytes, CancellationToken ct)
        {
            Calls.Add(new Call(id, filename, position, tempPath, maxBytes));
            if (Throw is not null)
            {
                if (WriteBeforeThrow) File.WriteAllBytes(tempPath, Bytes);
                throw Throw;
            }
            File.WriteAllBytes(tempPath, Bytes);
            OnSave?.Invoke();
            return Task.FromResult(new SavedAttachment(tempPath, Name, ReportedSize ?? Bytes.Length));
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var p in kv) Messages.Add(p.Value?.ToString() ?? "");
        }
    }
}

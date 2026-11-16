using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AttachmentLinksTests : IDisposable
{
    private const string Original = "Bericht fÃ¼r Q3.pdf";
    private const long MiB = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-links-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLog _log = new();
    private readonly List<AttachmentLinks> _made = [];

    private string Folder => Path.Combine(_root, "links");

    public void Dispose()
    {
        foreach (var l in _made) l.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private AttachmentLinks Make(Action<string>? ensure = null, ILinkFileSystem? files = null, string? folder = null)
    {
        var links = new AttachmentLinks(new AttachmentLinksOptions
        {
            Folder = folder ?? Folder,
            EnsurePrivateFolder = ensure ?? (d => Directory.CreateDirectory(d)),
            Files = files ?? LinkFileSystem.Instance,
            Time = _time,
            Logger = _log,
        });
        _made.Add(links);
        return links;
    }

    private static string Saved(AttachmentLinks links, int bytes = 3)
    {
        var path = links.PrepareTempPath();
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static AttachmentLink Open(AttachmentLinks links, long size = 3, string name = "a.pdf") =>
        links.Add(Saved(links), name, size);

    private static string Hex(char c) => new(c, 32);

    private sealed class TestFs : ILinkFileSystem
    {
        public bool FailDeletes;
        public int DeleteCalls;
        public Func<bool>? Link;

        public bool IsLink(string folder) => Link is { } l ? l() : LinkFileSystem.Instance.IsLink(folder);
        public IEnumerable<string> EnumerateFiles(string folder) => LinkFileSystem.Instance.EnumerateFiles(folder);

        public void DeleteFile(string path)
        {
            DeleteCalls++;
            if (FailDeletes) throw new IOException("locked " + path);
            LinkFileSystem.Instance.DeleteFile(path);
        }
    }

    // ---- Tokens ----

    [Fact]
    public void NewToken_Is43UrlSafeCharsAndUnique()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            var t = AttachmentLinks.NewToken();
            Assert.Equal(43, t.Length);
            Assert.Matches(@"^[A-Za-z0-9_-]{43}\z", t);
            Assert.True(seen.Add(t));
        }
    }

    [Fact]
    public void IsWellFormedToken()
    {
        var t = AttachmentLinks.NewToken();
        Assert.True(AttachmentLinks.IsWellFormedToken(t));
        Assert.False(AttachmentLinks.IsWellFormedToken(null));
        Assert.False(AttachmentLinks.IsWellFormedToken(""));
        Assert.False(AttachmentLinks.IsWellFormedToken(t[..42]));
        Assert.False(AttachmentLinks.IsWellFormedToken(t + "A"));
        Assert.False(AttachmentLinks.IsWellFormedToken(t[..42] + "="));
        Assert.False(AttachmentLinks.IsWellFormedToken(t[..42] + "/"));
        Assert.False(AttachmentLinks.IsWellFormedToken(t[..42] + "+"));
        Assert.False(AttachmentLinks.IsWellFormedToken(t + "\n"));
    }

    [Fact]
    public void Add_ReturnsLinkAsGiven()
    {
        var links = Make();
        var path = Saved(links);
        var link = links.Add(path, Original, 3);
        Assert.True(AttachmentLinks.IsWellFormedToken(link.Token));
        Assert.Equal(path, link.TempPath);
        Assert.Equal(Original, link.FileName);
        Assert.Equal(3, link.Size);
        Assert.Equal(ManualTimeProvider.T0, link.CreatedAt);
        Assert.Equal(ManualTimeProvider.T0 + TimeSpan.FromMinutes(10), link.ExpiresAt);
        Assert.Equal(1, links.OpenCount);
        Assert.Equal(3, links.OpenBytes);
    }

    [Fact]
    public void ToString_HidesTokenAndPath()
    {
        var links = Make();
        var link = Open(links);
        var text = link.ToString();
        Assert.DoesNotContain(link.Token, text);
        Assert.DoesNotContain(link.TempPath, text);
        var result = AttachmentLinkResult.Create(link, 8765, TimeZoneInfo.Utc);
        Assert.DoesNotContain(link.Token, result.ToString());
        Assert.DoesNotContain("http", result.ToString());
    }

    // ---- Single use and expiry ----

    [Fact]
    public void TryConsume_IsSingleUse_FileDeletedOnComplete()
    {
        var links = Make();
        var link = Open(links);
        var got = links.TryConsume(link.Token);
        Assert.Equal(link, got);
        Assert.Null(links.TryConsume(link.Token));
        Assert.Equal(1, links.OpenCount);
        Assert.True(File.Exists(link.TempPath));
        links.Complete(got!);
        Assert.Equal(0, links.OpenCount);
        Assert.False(File.Exists(link.TempPath));
        links.Complete(got!);
    }

    [Fact]
    public async Task TryConsume_RaceYieldsExactlyOne()
    {
        var links = Make();
        for (var round = 0; round < 50; round++)
        {
            var link = Open(links);
            var wins = 0;
            using var barrier = new Barrier(16);
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                if (links.TryConsume(link.Token) is not null) Interlocked.Increment(ref wins);
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(1, wins);
            links.Complete(link);
            _time.Advance(TimeSpan.FromMinutes(11));
        }
    }

    [Fact]
    public void TryConsume_UnknownOrMalformed_Null()
    {
        var links = Make();
        Open(links);
        Assert.Null(links.TryConsume(AttachmentLinks.NewToken()));
        Assert.Null(links.TryConsume("short"));
        Assert.Null(links.TryConsume(null));
        Assert.Null(links.TryConsume(""));
        Assert.Equal(1, links.OpenCount);
    }

    [Fact]
    public void Expiry_AtExactlyTenMinutes()
    {
        var links = Make();
        var a = Open(links);
        _time.Advance(TimeSpan.FromSeconds(599));
        var got = links.TryConsume(a.Token);
        Assert.NotNull(got);
        links.Complete(got);

        var b = Open(links);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(links.TryConsume(b.Token));
        Assert.False(File.Exists(b.TempPath));
    }

    [Fact]
    public void SweepTimer_RemovesExpiredLinks()
    {
        var links = Make();
        var a = Open(links);
        for (var i = 0; i < 9; i++) _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, links.OpenCount);
        Assert.True(File.Exists(a.TempPath));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, links.OpenCount);
        Assert.False(File.Exists(a.TempPath));
    }

    // ---- Limits and texts ----

    [Fact]
    public void Count_LimitAtTen()
    {
        var links = Make();
        var open = Enumerable.Range(0, 10).Select(_ => Open(links)).ToList();
        var ex = Assert.Throws<AttachmentLinkException>(links.CheckCanCreate);
        Assert.Equal(LinkRefusal.TooManyOpen, ex.Reason);
        Assert.Equal("Too many open download links (10). Download them or wait until they expire (10 minutes), then try again.", ex.Message);

        var extra = Saved(links);
        var ex2 = Assert.Throws<AttachmentLinkException>(() => links.Add(extra, "x.pdf", 3));
        Assert.Equal(LinkRefusal.TooManyOpen, ex2.Reason);
        Assert.False(File.Exists(extra));

        var got = links.TryConsume(open[0].Token);
        Assert.NotNull(got);
        Assert.Throws<AttachmentLinkException>(links.CheckCanCreate);
        links.Complete(got!);
        links.CheckCanCreate();
    }

    [Fact]
    public void ExpiredLinks_DontCount()
    {
        var links = Make();
        var open = Enumerable.Range(0, 10).Select(_ => Open(links)).ToList();
        _time.Advance(TimeSpan.FromMinutes(10));
        links.CheckCanCreate();
        Assert.All(open, l => Assert.False(File.Exists(l.TempPath)));
    }

    [Fact]
    public void TooBig_RefusedAndFileDeleted_ExactMaxAllowed()
    {
        var links = Make();
        var path = Saved(links);
        var ex = Assert.Throws<AttachmentLinkException>(() => links.Add(path, "a.pdf", AttachmentLinks.MaxLinkBytes + 1));
        Assert.Equal(LinkRefusal.TooBig, ex.Reason);
        Assert.Equal("The attachment is 101 MB; attachment_link handles up to 100 MB. Use get_attachment to save it on the Windows side.", ex.Message);
        Assert.False(File.Exists(path));

        var ok = links.Add(Saved(links), "a.pdf", AttachmentLinks.MaxLinkBytes);
        Assert.Equal(AttachmentLinks.MaxLinkBytes, ok.Size);
    }

    [Fact]
    public void Bytes_LimitIs200MiB()
    {
        var links = Make();
        links.Add(Saved(links), "a", 100 * MiB);
        links.Add(Saved(links), "b", 60 * MiB);
        var path = Saved(links);
        var ex = Assert.Throws<AttachmentLinkException>(() => links.Add(path, "c", 50 * MiB));
        Assert.Equal(LinkRefusal.TooManyBytes, ex.Reason);
        Assert.Equal("The open download links already hold 200 MB. Download them or wait until they expire, then try again.", ex.Message);
        Assert.False(File.Exists(path));

        links.Add(Saved(links), "d", 40 * MiB);
        Assert.Equal(200 * MiB, links.OpenBytes);
        var ex2 = Assert.Throws<AttachmentLinkException>(links.CheckCanCreate);
        Assert.Equal(LinkRefusal.TooManyBytes, ex2.Reason);
    }

    [Fact]
    public void Order_SizeThenCountThenBytesThenRate()
    {
        var links = Make();
        for (var i = 0; i < 10; i++) Open(links);
        var ex = Assert.Throws<AttachmentLinkException>(() => links.Add(Saved(links), "a", AttachmentLinks.MaxLinkBytes + 1));
        Assert.Equal(LinkRefusal.TooBig, ex.Reason);

        var more = Make(folder: Path.Combine(_root, "more"));
        for (var i = 0; i < 2; i++) more.Add(Saved(more), "big", 100 * MiB);
        for (var i = 0; i < 8; i++) more.Add(Saved(more), "z", 0);
        Assert.Equal(10, more.OpenCount);
        Assert.Equal(200 * MiB, more.OpenBytes);
        var ex2 = Assert.Throws<AttachmentLinkException>(more.CheckCanCreate);
        Assert.Equal(LinkRefusal.TooManyOpen, ex2.Reason);
    }

    private void CreateAndConsume(AttachmentLinks links, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var l = Open(links);
            links.Complete(links.TryConsume(l.Token)!);
        }
    }

    [Fact]
    public void RateWindow_TwentyPerTenMinutes()
    {
        var links = Make();
        CreateAndConsume(links, 10);
        _time.Advance(TimeSpan.FromMinutes(1));
        CreateAndConsume(links, 10);

        _time.Advance(TimeSpan.FromMinutes(1));
        var ex = Assert.Throws<AttachmentLinkException>(links.CheckCanCreate);
        Assert.Equal(LinkRefusal.RateLimited, ex.Reason);
        Assert.Equal("Too many download links in the last 10 minutes (20). Try again in 8 min.", ex.Message);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Too many download links in the last 10 minutes (20). Try again in 8 min.",
            Assert.Throws<AttachmentLinkException>(links.CheckCanCreate).Message);

        _time.Advance(TimeSpan.FromSeconds(7 * 60 + 29));
        Assert.Equal(ManualTimeProvider.T0 + TimeSpan.FromSeconds(9 * 60 + 59), _time.GetUtcNow());
        Assert.Equal("Too many download links in the last 10 minutes (20). Try again in 1 min.",
            Assert.Throws<AttachmentLinkException>(links.CheckCanCreate).Message);

        _time.Advance(TimeSpan.FromSeconds(1));
        links.CheckCanCreate();
        CreateAndConsume(links, 10);
        var p = Saved(links);
        var ex2 = Assert.Throws<AttachmentLinkException>(() => links.Add(p, "a", 3));
        Assert.Equal(LinkRefusal.RateLimited, ex2.Reason);
        Assert.Equal("Too many download links in the last 10 minutes (20). Try again in 1 min.", ex2.Message);
    }

    [Fact]
    public void FailedAdds_DontCountTowardRate()
    {
        var links = Make();
        for (var i = 0; i < 10; i++)
            Assert.Throws<AttachmentLinkException>(() => links.Add(Saved(links), "a", AttachmentLinks.MaxLinkBytes + 1));
        CreateAndConsume(links, 10);
        CreateAndConsume(links, 10);
        Assert.Equal(LinkRefusal.RateLimited, Assert.Throws<AttachmentLinkException>(links.CheckCanCreate).Reason);
    }

    [Fact]
    public void Clear_DoesNotResetRate()
    {
        var links = Make();
        CreateAndConsume(links, 10);
        CreateAndConsume(links, 10);
        links.Clear();
        Assert.Equal(LinkRefusal.RateLimited, Assert.Throws<AttachmentLinkException>(links.CheckCanCreate).Reason);
    }

    [Theory]
    [InlineData(1L, "1")]
    [InlineData(1048576L, "1")]
    [InlineData(1048577L, "2")]
    [InlineData(150L * 1048576, "150")]
    public void TooBigText_RoundsUp(long bytes, string mb)
    {
        Assert.Equal($"The attachment is {mb} MB; attachment_link handles up to 100 MB. Use get_attachment to save it on the Windows side.", LinkTexts.TooBig(bytes));
    }

    [Fact]
    public void TooBigText_NegativeThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkTexts.TooBig(-1));

    // ---- Add validation ----

    [Fact]
    public void Add_RejectsForeignPaths_KeepsFiles()
    {
        var links = Make();
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, LinkPaths.NewFileName());
        File.WriteAllBytes(outside, [1]);
        Assert.Throws<ArgumentException>(() => links.Add(outside, "a", 1));
        Assert.True(File.Exists(outside));

        Directory.CreateDirectory(Folder);
        var named = Path.Combine(Folder, "report.pdf");
        File.WriteAllBytes(named, [1]);
        Assert.Throws<ArgumentException>(() => links.Add(named, "a", 1));
        Assert.True(File.Exists(named));

        var upper = Path.Combine(Folder, Hex('A') + ".bin");
        File.WriteAllBytes(upper, [1]);
        Assert.Throws<ArgumentException>(() => links.Add(upper, "a", 1));
        Assert.True(File.Exists(upper));

        var good = Saved(links);
        Assert.Throws<ArgumentNullException>(() => links.Add(null!, "a", 1));
        Assert.Throws<ArgumentNullException>(() => links.Add(good, null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => links.Add(good, "a", -1));
        Assert.True(File.Exists(good));
    }

    // ---- Folder ----

    [Fact]
    public void PrepareTempPath_CallsHookAndReturnsFreshName()
    {
        var calls = new List<string>();
        var links = Make(ensure: d =>
        {
            calls.Add(d);
            Directory.CreateDirectory(d);
        });
        var a = links.PrepareTempPath();
        Assert.Equal([links.Folder], calls);
        Assert.True(Directory.Exists(Folder));
        Assert.Equal(links.Folder, Path.GetDirectoryName(a));
        Assert.True(LinkPaths.IsTempFileName(Path.GetFileName(a)));
        var b = links.PrepareTempPath();
        Assert.NotEqual(a, b);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void PrepareTempPath_HookThrows_FolderUnusable()
    {
        var inner = new UnauthorizedAccessException("x");
        var links = Make(ensure: _ => throw inner);
        var ex = Assert.Throws<AttachmentLinkException>(links.PrepareTempPath);
        Assert.Equal(LinkRefusal.FolderUnusable, ex.Reason);
        Assert.Equal("Clippy can't use its private download folder. Use get_attachment instead.", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void PrepareTempPath_LinkFolder_FolderUnusable()
    {
        var links = Make(files: new TestFs { Link = () => true });
        Assert.Equal(LinkRefusal.FolderUnusable, Assert.Throws<AttachmentLinkException>(links.PrepareTempPath).Reason);
        var links2 = Make(files: new TestFs { Link = () => throw new IOException("x") });
        Assert.Equal(LinkRefusal.FolderUnusable, Assert.Throws<AttachmentLinkException>(links2.PrepareTempPath).Reason);
    }

    [Fact]
    public void PrepareTempPath_FolderIsAFile_FolderUnusable()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Folder, [1]);
        var links = Make(ensure: _ => { });
        Assert.Equal(LinkRefusal.FolderUnusable, Assert.Throws<AttachmentLinkException>(links.PrepareTempPath).Reason);
    }

    [Fact]
    [Trait("os", "windows")]
    public void Junction_Refused_NothingDeletedThroughIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, LinkPaths.NewFileName());
        File.WriteAllBytes(victim, [1]);
        var junction = Path.Combine(_root, "junction");
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(junction);
        psi.ArgumentList.Add(outside);
        using (var proc = Process.Start(psi)!)
        {
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        try
        {
            var links = Make(folder: junction);
            Assert.Equal(LinkRefusal.FolderUnusable, Assert.Throws<AttachmentLinkException>(links.PrepareTempPath).Reason);
            links.CleanUpAtStart();
            Assert.True(File.Exists(victim));
            Assert.Contains(_log.Lines, l => l.Contains("junction or symbolic link"));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    // ---- Cleanup and retry ----

    [Fact]
    public void Clear_DeletesEverything()
    {
        var links = Make();
        var open = Enumerable.Range(0, 3).Select(_ => Open(links)).ToList();
        links.Clear();
        Assert.Equal(0, links.OpenCount);
        Assert.All(open, l =>
        {
            Assert.False(File.Exists(l.TempPath));
            Assert.Null(links.TryConsume(l.Token));
        });
    }

    [Fact]
    public void CleanUpAtStart_DeletesOnlyTempNamedFilesAtTopLevel()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "sub"));
        var a = Path.Combine(Folder, Hex('d') + ".bin");
        var b = Path.Combine(Folder, "0123456789abcdef0123456789abcdef.bin");
        var notes = Path.Combine(Folder, "notes.txt");
        var upper = Path.Combine(Folder, Hex('A') + ".bin");
        var nested = Path.Combine(Folder, "sub", Hex('b') + ".bin");
        foreach (var p in new[] { a, b, notes, upper, nested }) File.WriteAllBytes(p, [1]);

        Make().CleanUpAtStart();

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(upper));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void CleanUpAtStart_MissingFolderIsCreated()
    {
        Make().CleanUpAtStart();
        Assert.True(Directory.Exists(Folder));
    }

    [Fact]
    public void CleanUpAtStart_HookThrows_WarnsAndDeletesNothing()
    {
        Directory.CreateDirectory(Folder);
        var a = Path.Combine(Folder, Hex('a') + ".bin");
        File.WriteAllBytes(a, [1]);
        var links = Make(ensure: _ => throw new IOException("nope"));
        links.CleanUpAtStart();
        Assert.True(File.Exists(a));
        Assert.Contains("Download link folder not usable at start-up (IOException); cleanup skipped.", _log.Lines);
    }

    [Fact]
    public void FailedDelete_RetriedAtNextSweep()
    {
        var fs = new TestFs();
        var links = Make(files: fs);
        var link = Open(links);
        var got = links.TryConsume(link.Token)!;
        fs.FailDeletes = true;
        links.Complete(got);
        Assert.True(File.Exists(link.TempPath));
        Assert.Equal(["Could not delete a download link file (IOException); retrying at the next sweep."], _log.Lines);

        fs.FailDeletes = false;
        fs.DeleteCalls = 0;
        _time.Advance(AttachmentLinks.SweepInterval);
        Assert.False(File.Exists(link.TempPath));
        Assert.Equal(1, fs.DeleteCalls);
        _time.Advance(AttachmentLinks.SweepInterval);
        Assert.Equal(1, fs.DeleteCalls);
    }

    [Fact]
    public void Discard_DeletesTempFile_RejectsOthers()
    {
        var links = Make();
        var path = Saved(links);
        links.Discard(path);
        Assert.False(File.Exists(path));
        Assert.Throws<ArgumentException>(() => links.Discard(Path.Combine(Folder, "notes.txt")));
        Assert.Throws<ArgumentNullException>(() => links.Discard(null!));
    }

    [Fact]
    public void Dispose_CleansUpAndRefusesFurtherUse()
    {
        var links = Make();
        var consumed = Open(links);
        var got = links.TryConsume(consumed.Token)!;
        var open = Open(links);
        var other = Open(links);
        links.Dispose();
        Assert.False(File.Exists(open.TempPath));
        Assert.False(File.Exists(other.TempPath));
        Assert.Throws<ObjectDisposedException>(() => links.Add(Path.Combine(Folder, LinkPaths.NewFileName()), "a", 1));
        Assert.Throws<ObjectDisposedException>(links.CheckCanCreate);
        Assert.Throws<ObjectDisposedException>(links.PrepareTempPath);
        Assert.Null(links.TryConsume(open.Token));
        links.Complete(got);
        Assert.False(File.Exists(consumed.TempPath));
        links.Dispose();

        var before = _log.Lines.Count;
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(before, _log.Lines.Count);
    }

    [Fact]
    public void Streaming_CountsAgainstCountAndBytesUntilComplete()
    {
        var links = Make();
        var big = links.Add(Saved(links), "big", 100 * MiB);
        var big2 = links.Add(Saved(links), "big2", 100 * MiB);
        var got = links.TryConsume(big.Token)!;
        Assert.Null(links.TryConsume(big.Token));
        Assert.Equal(2, links.OpenCount);
        Assert.Equal(200 * MiB, links.OpenBytes);
        var ex = Assert.Throws<AttachmentLinkException>(links.CheckCanCreate);
        Assert.Equal(LinkRefusal.TooManyBytes, ex.Reason);
        links.Complete(got);
        Assert.Equal(1, links.OpenCount);
        Assert.Equal(100 * MiB, links.OpenBytes);
        links.CheckCanCreate();
        Assert.NotNull(big2);

        var many = Make(folder: Path.Combine(_root, "many"));
        var streams = Enumerable.Range(0, 10).Select(_ => many.TryConsume(Open(many).Token)!).ToList();
        Assert.Equal(10, many.OpenCount);
        Assert.Equal(LinkRefusal.TooManyOpen, Assert.Throws<AttachmentLinkException>(many.CheckCanCreate).Reason);
        many.Complete(streams[0]);
        many.CheckCanCreate();
    }

    [Fact]
    public void Add_AfterDispose_DeletesFileAndThrows()
    {
        var links = Make();
        var path = Saved(links);
        links.Dispose();
        Assert.Throws<ObjectDisposedException>(() => links.Add(path, "a", 3));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Add_AfterClear_StillWorksButClearedLinksStayGone()
    {
        var links = Make();
        var a = Open(links);
        links.Clear();
        Assert.Null(links.TryConsume(a.Token));
        Assert.Equal(0, links.OpenCount);
        Assert.False(File.Exists(a.TempPath));
    }

    [Fact]
    public void Constructor_Validates()
    {
        Assert.Throws<ArgumentNullException>(() => new AttachmentLinks(null!));
        Assert.Throws<ArgumentException>(() => new AttachmentLinks(new AttachmentLinksOptions { Folder = "rel", EnsurePrivateFolder = _ => { } }));
        Assert.Throws<ArgumentException>(() => new AttachmentLinks(new AttachmentLinksOptions { Folder = " ", EnsurePrivateFolder = _ => { } }));
    }

    // ---- Logs ----

    [Fact]
    public void Logs_NeverCarrySecrets()
    {
        var fs = new TestFs();
        var links = Make(files: fs);
        var secrets = new List<string> { Original, Folder, _root };

        var a = links.Add(Saved(links), Original, 3);
        secrets.Add(a.Token);
        secrets.Add(a.TempPath);
        links.Complete(links.TryConsume(a.Token)!);

        var b = links.Add(Saved(links), Original, 3);
        secrets.Add(b.Token);
        secrets.Add(b.TempPath);
        _time.Advance(TimeSpan.FromMinutes(10));

        for (var i = 0; i < 10; i++)
        {
            var l = links.Add(Saved(links), Original, 3);
            secrets.Add(l.Token);
            secrets.Add(l.TempPath);
        }
        var extra = Saved(links);
        secrets.Add(extra);
        Assert.Throws<AttachmentLinkException>(() => links.Add(extra, Original, 3));
        links.Clear();

        File.WriteAllBytes(Path.Combine(Folder, Hex('c') + ".bin"), [1]);
        secrets.Add(Hex('c'));
        links.CleanUpAtStart();

        var c = links.Add(Saved(links), Original, 3);
        secrets.Add(c.Token);
        secrets.Add(c.TempPath);
        fs.FailDeletes = true;
        links.Complete(links.TryConsume(c.Token)!);

        Assert.NotEmpty(_log.Lines);
        foreach (var line in _log.Lines)
            foreach (var s in secrets)
                Assert.DoesNotContain(s, line);
    }

    [Fact]
    public void Result_SerializesSnakeCaseNames()
    {
        var links = Make();
        var link = Open(links);
        var json = JsonSerializer.Serialize(AttachmentLinkResult.Create(link, 8765, TimeZoneInfo.Utc));
        var names = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["url", "filename", "size", "expires_at", "curl", "note"], names);
        Assert.Matches(new Regex(@"/files/[A-Za-z0-9_-]{43}"), json);
    }
}

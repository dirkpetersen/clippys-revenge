using System.Diagnostics;

namespace ClippysRevenge.Core.Tests;

public class SyncedFileReaderTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "clippy-od-" + Guid.NewGuid().ToString("N"));
    private readonly string _mount;
    private readonly SyncedRoot _root;

    public SyncedFileReaderTests()
    {
        _mount = Path.Combine(_tmp, "mount");
        Directory.CreateDirectory(_mount);
        _root = new SyncedRoot("https://contoso.sharepoint.com/sites/F/Shared Documents/", _mount, "teamsite", false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_mount, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private MappedLink.Local Link(string path) => new(path, _root);

    private static void SymlinkOrSkip(Action create)
    {
        try { create(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symlink creation not permitted");
        }
    }

    [Fact]
    public void Reads()
    {
        var p = Write(Path.Combine("Reports", "a.txt"), "hello");
        var b = Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(Link(p), 1000, CancellationToken.None));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(b.Data));
        Assert.Equal("a.txt", b.FileName);
    }

    [Fact]
    public void TooLarge_ByMetadata_AndExactMax()
    {
        var p = Write("big.txt", "12345678901");
        Assert.Equal(11, Assert.IsType<SyncedFileResult.TooLarge>(SyncedFileReader.Read(Link(p), 10, CancellationToken.None)).Length);
        var q = Write("ok.txt", "1234567890");
        Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(Link(q), 10, CancellationToken.None));
    }

    [Fact]
    public void LargerThanOneChunk_ReadsAll()
    {
        var p = Path.Combine(_mount, "chunks.bin");
        var data = new byte[200_000];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(p, data);
        var b = Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(Link(p), 1_000_000, CancellationToken.None));
        Assert.Equal(data, b.Data);
    }

    [Fact]
    public void EmptyFile_ReadsEmpty()
    {
        var p = Write("empty.txt", "");
        var b = Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(Link(p), 10, CancellationToken.None));
        Assert.Empty(b.Data);
    }

    [Fact]
    public void MissingFolderOutsideAndAds_NotSynced()
    {
        Directory.CreateDirectory(Path.Combine(_mount, "dir"));
        var outside = Path.Combine(_tmp, "outside.txt");
        File.WriteAllText(outside, "x");
        var a = Write("a.txt", "x");
        foreach (var p in new[] { Path.Combine(_mount, "nope.txt"), Path.Combine(_mount, "dir"), outside, a + ":secret" })
            Assert.IsType<SyncedFileResult.NotSynced>(SyncedFileReader.Read(Link(p), 1000, CancellationToken.None));
    }

    [Fact]
    public void OpenWhileLocked_Reads()
    {
        var p = Write("locked.txt", "data");
        using var held = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(Link(p), 1000, CancellationToken.None));
    }

    [Fact]
    public void SymlinkedDirBelowMount_NotSynced()
    {
        var outside = Path.Combine(_tmp, "outsideDir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "s.txt"), "x");
        var link = Path.Combine(_mount, "link");
        SymlinkOrSkip(() => Directory.CreateSymbolicLink(link, outside));
        Assert.IsType<SyncedFileResult.NotSynced>(SyncedFileReader.Read(Link(Path.Combine(link, "s.txt")), 1000, CancellationToken.None));
    }

    [Fact]
    [Trait("os", "windows")]
    public void JunctionBelowMount_NotSynced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var outside = Path.Combine(_tmp, "outsideDir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "s.txt"), "x");
        var link = Path.Combine(_mount, "j");
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(outside);
        using (var proc = Process.Start(psi)!) proc.WaitForExit();
        Assert.IsType<SyncedFileResult.NotSynced>(SyncedFileReader.Read(Link(Path.Combine(link, "s.txt")), 1000, CancellationToken.None));
    }

    [Fact]
    public void Cancelled_AndNullLink()
    {
        var p = Write("a.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SyncedFileReader.Read(Link(p), 1000, cts.Token));
        Assert.Throws<ArgumentNullException>(() => SyncedFileReader.Read(null!, 1000, CancellationToken.None));
    }

    [Fact]
    public void EndToEnd()
    {
        Write(Path.Combine("x", "a.txt"), "content");
        var root = new SyncedRoot("https://contoso.sharepoint.com/sites/F/Shared Documents/", _mount, "teamsite", false);
        var mapped = OneDriveMap.Map("https://contoso.sharepoint.com/sites/F/Shared%20Documents/x/a.txt", [root]);
        var local = Assert.IsType<MappedLink.Local>(mapped);
        var b = Assert.IsType<SyncedFileResult.Bytes>(SyncedFileReader.Read(local, 1000, CancellationToken.None));
        Assert.Equal("content", System.Text.Encoding.UTF8.GetString(b.Data));
    }
}

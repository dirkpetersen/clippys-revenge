using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public sealed class AiFilesTests : IDisposable
{
    private const string M = "FIXTURE-SECRET-4711";

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(logLevel + "|" + formatter(state, exception));
    }

    private sealed class TestFs : ILinkFileSystem
    {
        public bool IsLinkResult { get; set; }
        public int DeleteThrowsTimes { get; set; }
        public bool EnumerateThrows { get; set; }

        public bool IsLink(string folder) => IsLinkResult || LinkFileSystem.Instance.IsLink(folder);

        public IEnumerable<string> EnumerateFiles(string folder)
        {
            if (EnumerateThrows) throw new IOException("C:\\secret\\" + M);
            return LinkFileSystem.Instance.EnumerateFiles(folder);
        }

        public void DeleteFile(string path)
        {
            if (DeleteThrowsTimes > 0)
            {
                DeleteThrowsTimes--;
                throw new IOException("C:\\secret\\" + M);
            }
            LinkFileSystem.Instance.DeleteFile(path);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-aifiles-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();
    private readonly TestFs _fs = new();
    private int _ensureCalls;
    private Exception? _ensureThrows;

    public AiFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Folder => Path.Combine(_dir, "ai-files");

    private AiFiles Make() => new(new AiFilesOptions
    {
        Folder = Folder,
        EnsurePrivateFolder = d =>
        {
            _ensureCalls++;
            if (_ensureThrows is not null) throw _ensureThrows;
            Directory.CreateDirectory(d);
        },
        Files = _fs,
        Logger = _log,
    });

    private static string Hex(char c = 'a') => new(c, 32);

    [Fact]
    public void Default_BuildsTheFolder()
    {
        var x = Path.GetFullPath(_dir);
        Assert.Equal(Path.Combine(x, "ClippysRevenge", "ai-files"), AiFilePaths.Default(x).Folder);
        Assert.Throws<ArgumentException>(() => AiFilePaths.Default(""));
        Assert.Throws<ArgumentException>(() => AiFilePaths.Default("rel\\x"));
    }

    [Fact]
    public void NewFileName_ShapeAndUniqueness()
    {
        Assert.Matches(@"^[0-9a-f]{32}\.bin$", AiFilePaths.NewFileName(false));
        Assert.Matches(@"^[0-9a-f]{32}\.msg$", AiFilePaths.NewFileName(true));
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => AiFilePaths.NewFileName(false)).Distinct().Count());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.bin", true)]
    [InlineData("0123456789abcdef0123456789abcdef.msg", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef.bin", false)]
    [InlineData("0123456789abcdef0123456789abcdef.BIN", false)]
    [InlineData("0123456789abcdef0123456789abcdef.tmp", false)]
    [InlineData("0123456789abcdef0123456789abcde.bin", false)]
    [InlineData("0123456789abcdef0123456789abcdef0.bin", false)]
    [InlineData("x.bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTempFileName(string? name, bool expected) => Assert.Equal(expected, AiFilePaths.IsTempFileName(name));

    [Fact]
    public void PrepareTempPath_EnsuresFolderAndReturnsAFreshName()
    {
        var ai = Make();
        var p = ai.PrepareTempPath(false);
        Assert.Equal(1, _ensureCalls);
        Assert.True(Directory.Exists(Folder));
        Assert.Equal(ai.Folder, Path.GetDirectoryName(p));
        Assert.True(AiFilePaths.IsTempFileName(Path.GetFileName(p)));
        Assert.EndsWith(".bin", p);
        Assert.False(File.Exists(p));
        Assert.EndsWith(".msg", ai.PrepareTempPath(true));
    }

    [Fact]
    public void EnsureFailure_IsWrappedWithAConstantMessage()
    {
        _ensureThrows = new IOException("C:\\secret\\" + M);
        var ex = Assert.Throws<AiFolderUnusableException>(() => Make().PrepareTempPath(false));
        Assert.Equal(AiFolderUnusableException.DefaultMessage, ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void LinkedFolder_IsRefused()
    {
        _fs.IsLinkResult = true;
        Assert.Throws<AiFolderUnusableException>(() => Make().PrepareTempPath(false));
    }

    [Fact]
    public void Discard_DeletesAndValidates()
    {
        var ai = Make();
        var p = ai.PrepareTempPath(false);
        File.WriteAllText(p, "x");
        ai.Discard(p);
        Assert.False(File.Exists(p));
        ai.Discard(p);

        var outside = Path.Combine(_dir, Hex() + ".bin");
        var e1 = Assert.Throws<ArgumentException>(() => ai.Discard(outside));
        var e2 = Assert.Throws<ArgumentException>(() => ai.Discard(Path.Combine(Folder, "keep.txt")));
        Assert.DoesNotContain(_dir, e1.Message);
        Assert.DoesNotContain(_dir, e2.Message);
        Assert.Throws<ArgumentNullException>(() => ai.Discard(null!));
    }

    [Fact]
    public void FailedDelete_IsRetriedAtTheNextPrepare()
    {
        var ai = Make();
        var p = ai.PrepareTempPath(false);
        File.WriteAllText(p, "x");
        _fs.DeleteThrowsTimes = 1;
        ai.Discard(p);
        Assert.Equal(1, ai.PendingRetries);
        Assert.Equal(["Warning|Could not delete an AI temp file (IOException); retrying before the next save."], _log.Lines);
        Assert.True(File.Exists(p));

        ai.PrepareTempPath(false);
        Assert.False(File.Exists(p));
        Assert.Equal(0, ai.PendingRetries);
    }

    [Fact]
    public void CleanUpAtStart_DeletesOnlyTempNamedFilesDirectlyInTheFolder()
    {
        Directory.CreateDirectory(Folder);
        var a = Path.Combine(Folder, Hex('a') + ".bin");
        var b = Path.Combine(Folder, Hex('b') + ".msg");
        var keep = Path.Combine(Folder, "keep.txt");
        var upper = Path.Combine(Folder, Hex('C').ToUpperInvariant() + ".BIN");
        var sub = Path.Combine(Folder, "sub");
        Directory.CreateDirectory(sub);
        var nested = Path.Combine(sub, Hex('d') + ".bin");
        foreach (var f in new[] { a, b, keep, upper, nested }) File.WriteAllText(f, "x");

        Make().CleanUpAtStart();

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(upper));
        Assert.True(File.Exists(nested));
        Assert.Equal(["Debug|AI file folder cleaned (2 files)."], _log.Lines);
    }

    [Fact]
    public void CleanUpAtStart_EnsureFails_NothingDeleted()
    {
        Directory.CreateDirectory(Folder);
        var a = Path.Combine(Folder, Hex() + ".bin");
        File.WriteAllText(a, "x");
        _ensureThrows = new IOException("C:\\secret\\" + M);
        Make().CleanUpAtStart();
        Assert.True(File.Exists(a));
        Assert.Equal(["Warning|AI file folder not usable at start-up (IOException); cleanup skipped."], _log.Lines);
    }

    [Fact]
    public void CleanUpAtStart_Link_NothingDeleted()
    {
        Directory.CreateDirectory(Folder);
        var a = Path.Combine(Folder, Hex() + ".bin");
        File.WriteAllText(a, "x");
        _fs.IsLinkResult = true;
        Make().CleanUpAtStart();
        Assert.True(File.Exists(a));
        Assert.Equal(["Warning|AI file folder is a junction or symbolic link; cleanup skipped."], _log.Lines);
    }

    [Fact]
    public void CleanUpAtStart_EnumerationFailure_DoesNotThrow()
    {
        _fs.EnumerateThrows = true;
        Make().CleanUpAtStart();
        Assert.Equal(["Warning|AI file cleanup failed (IOException)."], _log.Lines);
    }

    [Fact]
    public void Ctor_Validates()
    {
        Assert.Throws<ArgumentNullException>(() => new AiFiles(null!));
        Assert.Throws<ArgumentException>(() => new AiFiles(new AiFilesOptions
        {
            Folder = "rel\\ai-files",
            EnsurePrivateFolder = _ => { },
        }));
    }

    [Fact]
    public void Logs_NeverHoldPathsNamesOrMarkers()
    {
        var ai = Make();
        var p = ai.PrepareTempPath(false);
        File.WriteAllText(p, "x");
        _fs.DeleteThrowsTimes = 1;
        ai.Discard(p);
        File.WriteAllText(Path.Combine(Folder, Hex('e') + ".msg"), "x");
        ai.CleanUpAtStart();
        _fs.EnumerateThrows = true;
        ai.CleanUpAtStart();
        _fs.EnumerateThrows = false;
        _fs.IsLinkResult = true;
        ai.CleanUpAtStart();
        _fs.IsLinkResult = false;
        _ensureThrows = new IOException("C:\\secret\\" + M);
        ai.CleanUpAtStart();

        Assert.NotEmpty(_log.Lines);
        foreach (var line in _log.Lines)
        {
            Assert.DoesNotContain(_dir, line);
            Assert.DoesNotContain("ai-files", line);
            Assert.DoesNotContain(".bin", line);
            Assert.DoesNotContain(".msg", line);
            Assert.DoesNotContain(M, line);
            Assert.False(Regex.IsMatch(line, "[0-9a-f]{32}"), line);
        }
    }
}

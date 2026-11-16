using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class OneDriveScopesTests
{
    private sealed class FakeRegistryData : IOneDriveRegistryData
    {
        public List<OneDriveScopeValues> Scopes { get; } = [];
        public OneDrivePersonalValues? Personal { get; set; }
        public Exception? ScopesThrow { get; set; }
        public Exception? PersonalThrow { get; set; }

        public IReadOnlyList<OneDriveScopeValues> ReadScopes() => ScopesThrow is null ? Scopes : throw ScopesThrow;
        public OneDrivePersonalValues? ReadPersonal() => PersonalThrow is null ? Personal : throw PersonalThrow;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(logLevel + "|" + formatter(state, exception));
    }

    private const string Lib = "https://contoso.sharepoint.com/sites/Finance/Shared Documents/";

    private static OneDriveScopeValues Scope(string mount = @"C:\OD\Fin", string? ns = Lib, string? type = "teamsite",
        string? folder = null, string? remote = null) => new(mount, ns, type, folder, remote);

    private static IReadOnlyList<SyncedRoot> Build(FakeRegistryData d, ILogger? log = null, Func<string, bool>? exists = null)
        => OneDriveScopes.Build(d, log, exists ?? (_ => true));

    private static FakeRegistryData With(params OneDriveScopeValues[] s)
    {
        var d = new FakeRegistryData();
        d.Scopes.AddRange(s);
        return d;
    }

    [Fact]
    public void TeamLibrary()
    {
        var r = Assert.Single(Build(With(Scope())));
        Assert.Equal(new SyncedRoot(Lib, @"C:\OD\Fin", "teamsite", false), r);
    }

    [Fact]
    public void FolderScope()
    {
        var r = Assert.Single(Build(With(Scope(folder: "1", remote: "https://contoso.sharepoint.com/sites/Finance/Shared Documents/Budget"))));
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance/Shared Documents/Budget/", r.UrlPrefix);
        Assert.True(r.IsFolderScope);
    }

    [Fact]
    public void FolderFlagWithoutPath_UsesNamespace()
    {
        var r = Assert.Single(Build(With(Scope(folder: "1"))));
        Assert.Equal(Lib, r.UrlPrefix);
    }

    [Fact]
    public void NamespaceWithoutSlash_GetsOne()
    {
        var r = Assert.Single(Build(With(Scope(ns: "https://contoso.sharepoint.com/sites/F/Documents"))));
        Assert.EndsWith("/", r.UrlPrefix);
    }

    [Theory]
    [InlineData(@"C:\OD\Fin", "https://files.evil.example/lib/")]
    [InlineData(@"C:\OD\Fin", "http://contoso.sharepoint.com/sites/F/")]
    [InlineData(@"\\server\share\OD", Lib)]
    [InlineData(@"OD\Fin", Lib)]
    [InlineData(@"C:\OD\..\Windows", Lib)]
    [InlineData(@"\\?\C:\OD", Lib)]
    public void Rejected(string mount, string ns) => Assert.Empty(Build(With(Scope(mount, ns))));

    [Fact]
    public void MissingFolder_Skipped() =>
        Assert.Empty(Build(With(Scope()), exists: _ => false));

    [Fact]
    public void Duplicates_Collapsed_SamePrefixTwoMounts_Kept()
    {
        Assert.Single(Build(With(Scope(), Scope(), Scope(@"c:\od\FIN", Lib.ToUpperInvariant().Replace("HTTPS", "https")))));
        Assert.Equal(2, Build(With(Scope(@"C:\OD\A"), Scope(@"C:\OD\B"))).Count);
    }

    [Fact]
    public void ConsumerFallback()
    {
        var d = new FakeRegistryData { Personal = new(@"C:\OD\OneDrive", "abc123def4567890") };
        var r = Assert.Single(Build(d));
        Assert.Equal(new SyncedRoot("https://d.docs.live.net/abc123def4567890/", @"C:\OD\OneDrive", "personal", false), r);
    }

    [Fact]
    public void FallbackSuppressed_BadCid()
    {
        var d = With(Scope(@"C:\OD\C", "https://d.docs.live.net/abc123def4567890/"));
        d.Personal = new(@"C:\OD\OneDrive", "abc123def4567890");
        Assert.Single(Build(d));
        Assert.Empty(Build(new FakeRegistryData { Personal = new(@"C:\OD\OneDrive", "../x") }));
    }

    [Fact]
    public void Order_FallbackLast()
    {
        var d = With(Scope(@"C:\OD\A"), Scope(@"C:\OD\B"), Scope(@"C:\OD\C"));
        d.Personal = new(@"C:\OD\OneDrive", "abc");
        var r = Build(d);
        Assert.Equal([@"C:\OD\A", @"C:\OD\B", @"C:\OD\C", @"C:\OD\OneDrive"], r.Select(x => x.MountPoint).ToArray());
    }

    [Fact]
    public void ScopesThrow_EmptyAndOneWarning()
    {
        var log = new CapturingLogger();
        var d = With(Scope());
        d.ScopesThrow = new UnauthorizedAccessException(Lib + @" C:\OD\Fin");
        Assert.Empty(Build(d, log));
        var line = Assert.Single(log.Lines);
        Assert.StartsWith("Warning|", line);
        Assert.Contains("UnauthorizedAccessException", line);
        Assert.DoesNotContain("contoso", line);
        Assert.DoesNotContain(@"C:\OD", line);
    }

    [Fact]
    public void PersonalThrows_EmptyAndOneWarning()
    {
        var log = new CapturingLogger();
        var d = With(Scope());
        d.PersonalThrow = new IOException("x");
        Assert.Empty(Build(d, log));
        Assert.Single(log.Lines);
    }

    [Fact]
    public void DirectoryExistsThrows_ThatScopeSkipped()
    {
        var d = With(Scope(@"C:\OD\Bad"), Scope(@"C:\OD\Good"));
        var r = Build(d, exists: p => p.EndsWith("Bad") ? throw new IOException() : true);
        Assert.Equal(@"C:\OD\Good", Assert.Single(r).MountPoint);
    }
}

using System.Security.Cryptography;
using System.Text;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public sealed class SettingsApplierTests : IDisposable
{
    private sealed class PassProtector : ISecretProtector
    {
        public bool Fail;
        public byte[] Protect(byte[] p) => Fail ? throw new IOException("disk") : p;
        public byte[] Unprotect(byte[] d) => d;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null) Lines.Add(exception.ToString());
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-sa-" + Guid.NewGuid().ToString("N"));
    private readonly PassProtector _protector = new();
    private readonly SettingsStore _store;
    private readonly Settings _live;
    private readonly TokenHolder _tokens;
    private readonly CapturingLogger _log = new();
    private MailboxIndexes? _indexes;

    private readonly List<int> _restarts = [];
    private Func<int, Task<bool>> _restart;
    private int? _serverPort = 8765;
    private Action<bool> _startup = _ => { };
    private int _retention, _syncNow;
    private readonly string _oldToken;

    public SettingsApplierTests()
    {
        _store = new SettingsStore(Path.Combine(_root, "data"), _protector);
        var loaded = _store.Load();
        _live = loaded.Settings;
        _oldToken = loaded.Token;
        _tokens = new TokenHolder(loaded.Token);
        _restart = p => { _restarts.Add(p); return Task.FromResult(true); };
    }

    public void Dispose()
    {
        _indexes?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private Action? _aiChanged;

    private SettingsApplier Applier(bool withIndexes = false)
    {
        if (withIndexes)
            _indexes = new MailboxIndexes(Path.Combine(_root, "idx"), _live.SharedMailboxes.Where(m => m.Enabled).Select(m => m.Smtp));
        return new SettingsApplier(new SettingsApplyTargets(_live, _store, _tokens, p => _restart(p), () => _serverPort,
            e => _startup(e), _indexes, () => _retention++, () => _syncNow++, _aiChanged), _log);
    }

    [Fact]
    public async Task AiSettingsChanged_CalledOncePerChangingApply_NotForNoChangeOrErrors()
    {
        var calls = 0;
        _aiChanged = () => calls++;
        var applier = Applier();

        var none = await applier.ApplyAsync(new SettingsEditor(_live), TestContext.Current.CancellationToken);
        Assert.True(none.Ok);
        Assert.Equal(0, calls);

        var bad = new SettingsEditor(_live);
        bad.Draft.Port = 80;
        Assert.False((await applier.ApplyAsync(bad, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(0, calls);

        var good = new SettingsEditor(_live);
        good.Draft.AiDrafting.DailyCap = 31;
        Assert.True((await applier.ApplyAsync(good, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AiSettingsChanged_Throwing_DoesNotFailTheApply()
    {
        _aiChanged = () => throw new InvalidOperationException("secret-detail");
        var e = new SettingsEditor(_live);
        e.Draft.AiDrafting.DailyCap = 32;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.Empty(r.Warnings);
        Assert.Equal(32, _live.AiDrafting.DailyCap);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("secret-detail", StringComparison.Ordinal));
    }

    private string FileJson => File.ReadAllText(_store.SettingsPath);

    [Fact]
    public async Task Invalid_NothingApplied()
    {
        var before = FileJson;
        var e = new SettingsEditor(_live);
        e.Draft.Port = 80;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal("Port", r.Errors.Single().Field);
        Assert.Equal(before, FileJson);
        Assert.Empty(_restarts);
        Assert.Equal(0, _retention + _syncNow);
    }

    [Fact]
    public async Task NoChanges_NothingCalled()
    {
        var r = await Applier().ApplyAsync(new SettingsEditor(_live), TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.Empty(r.Warnings);
        Assert.Empty(_restarts);
        Assert.Equal(0, _retention + _syncNow);
    }

    [Fact]
    public async Task PortChange_RestartsThenSaves()
    {
        var e = new SettingsEditor(_live);
        e.Draft.Port = 9000;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.Equal([9000], _restarts);
        Assert.Equal(9000, Settings.FromJson(FileJson).Port);
        Assert.Equal(9000, _live.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortChange_Fails_RollsBack(bool throws)
    {
        var before = FileJson;
        _restart = p =>
        {
            _restarts.Add(p);
            if (p != 9000) return Task.FromResult(true);
            return throws ? throw new InvalidOperationException("nope") : Task.FromResult(false);
        };
        var e = new SettingsEditor(_live);
        e.Draft.Port = 9000;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal([9000, 8765], _restarts);
        Assert.Equal(new SettingsError("Port", "Port 9000 is in use or can't be opened. The server stays on port 8765."), r.Errors.Single());
        Assert.Equal(before, FileJson);
        Assert.Equal(8765, _live.Port);
    }

    [Fact]
    public async Task Token_SavedThenSet()
    {
        var e = new SettingsEditor(_live);
        e.RegenerateToken();
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.True(_tokens.Matches(e.PendingToken));
        Assert.False(_tokens.Matches(_oldToken));
        Assert.Equal(e.PendingToken, Encoding.UTF8.GetString(File.ReadAllBytes(_store.TokenPath)));
    }

    [Fact]
    public async Task Token_SaveFails_OldStays()
    {
        var e = new SettingsEditor(_live);
        e.RegenerateToken();
        e.Draft.RetentionMonths = 6;
        _protector.Fail = true;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal("Couldn't save the new auth token; the old one still works.", r.Warnings.Single());
        Assert.True(_tokens.Matches(_oldToken));
        Assert.Equal(6, _live.RetentionMonths);
    }

    [Fact]
    public async Task RetentionChange_CallsRetentionOnly()
    {
        var e = new SettingsEditor(_live);
        e.Draft.RetentionMonths = 6;
        await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal(1, _retention);
        Assert.Equal(0, _syncNow);
    }

    [Fact]
    public async Task FolderChange_CallsSyncNowOnly()
    {
        var e = new SettingsEditor(_live);
        e.Draft.IndexedFolders.Add(@"Inbox\Projects");
        await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal(0, _retention);
        Assert.Equal(1, _syncNow);
    }

    [Fact]
    public async Task LogLevelChange_NoSyncCalls()
    {
        var e = new SettingsEditor(_live);
        e.Draft.LogLevel = LogLevel.Debug;
        await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal(LogLevel.Debug, _live.LogLevel);
        Assert.Equal(0, _retention + _syncNow);
    }

    [Fact]
    public async Task StartWithWindows_Fails_Warns()
    {
        _startup = _ => throw new UnauthorizedAccessException("x");
        var e = new SettingsEditor(_live);
        e.Draft.StartWithWindows = !_live.StartWithWindows;
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Equal("Couldn't update the Windows startup entry: x", r.Warnings.Single());
        Assert.Equal(e.Draft.StartWithWindows, Settings.FromJson(FileJson).StartWithWindows);
    }

    [Fact]
    public async Task Mailbox_Enable_AddsIndex()
    {
        var applier = Applier(withIndexes: true);
        var e = new SettingsEditor(_live);
        e.AddMailbox("help@x.edu", "Help");
        var r = await applier.ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Empty(r.Warnings);
        Assert.Contains("help@x.edu", _indexes!.Mailboxes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mailbox_DisableDelete_RemovesFile(bool delete)
    {
        _live.AddOrEnableSharedMailbox("a@x.edu", "A");
        _store.Save(_live);
        var applier = Applier(withIndexes: true);
        var file = MailboxIndexes.PathFor(Path.Combine(_root, "idx"), "a@x.edu");
        Assert.True(File.Exists(file));
        var e = new SettingsEditor(_live);
        e.RemoveMailbox("a@x.edu", delete);
        var r = await applier.ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.Empty(r.Warnings);
        Assert.Equal(!delete, File.Exists(file));
        Assert.DoesNotContain("a@x.edu", _indexes!.Mailboxes);
    }

    [Fact]
    public async Task NullIndexes_MailboxChangeStillSaved()
    {
        var e = new SettingsEditor(_live);
        e.AddMailbox("help@x.edu", null);
        var r = await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.Single(Settings.FromJson(FileJson).SharedMailboxes);
    }

    [Fact]
    public async Task Concurrent_Applies_Serialized()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _restart = async p =>
        {
            lock (_restarts) _restarts.Add(p);
            started.TrySetResult();
            if (p == 9000) await gate.Task;
            return true;
        };
        var applier = Applier();
        var e1 = new SettingsEditor(_live);
        e1.Draft.Port = 9000;
        var t1 = applier.ApplyAsync(e1, TestContext.Current.CancellationToken);
        await started.Task;
        var e2 = new SettingsEditor(_live);
        e2.Draft.Port = 9001;
        var t2 = applier.ApplyAsync(e2, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        lock (_restarts) Assert.Equal([9000], _restarts);
        gate.SetResult(true);
        await Task.WhenAll(t1, t2);
        Assert.Equal([9000, 9001], _restarts);
    }

    [Fact]
    public async Task Logs_NoToken()
    {
        var e = new SettingsEditor(_live);
        e.RegenerateToken();
        e.Draft.Port = 9000;
        await Applier().ApplyAsync(e, TestContext.Current.CancellationToken);
        Assert.NotEmpty(_log.Lines);
        Assert.All(_log.Lines, l =>
        {
            Assert.DoesNotContain(e.PendingToken!, l);
            Assert.DoesNotContain(_oldToken, l);
        });
        Assert.Contains(_log.Lines, l => l.StartsWith("Settings applied:") && l.Contains("Port"));
    }
}

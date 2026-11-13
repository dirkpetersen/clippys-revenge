namespace ClippysRevenge.Core.Tests;

public sealed class UpdateHandoverTests : IDisposable
{
    private readonly UpdateHarness _h = new();
    private readonly FakeUpdateStarter _starter = new();
    private readonly PendingInstall _pending = UpdateHarness.Pending();

    public UpdateHandoverTests()
    {
        // The state a finished swap leaves: new exe in place, old exe renamed.
        File.Move(_h.Paths.ExePath, _h.Paths.OldExePath);
        File.WriteAllBytes(_h.Paths.ExePath, _h.NewBytes);
        _h.Store.Update(s => s.Pending = _pending);
    }

    public void Dispose() => _h.Dispose();

    private Task<HandoverOutcome> Run() =>
        UpdateHandover.RunAsync(_h.Paths, _h.Store, _pending, _starter.Start, _h.Clock);

    private Task<HandoverOutcome> Drive(Task<HandoverOutcome> t, TimeSpan? step = null, Action<int>? each = null) =>
        _h.Drive(t, step ?? UpdateHandover.Poll, each);

    private void AssertEveryStartWasTheInstalledExe()
    {
        Assert.NotEmpty(_starter.Paths);
        Assert.All(_starter.Paths, p => Assert.Equal(_h.Paths.ExePath, p));
    }

    private void AssertRolledBack()
    {
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.False(File.Exists(_h.Paths.OldExePath));
        var s = _h.Store.Load();
        Assert.Equal("0.0.6", s.FailedVersion);
        Assert.Null(s.Pending);
        Assert.Null(s.Staged);
        Assert.Equal(new UpdateNotice(UpdateNoticeKind.RolledBack, "0.0.6", "0.0.5"), s.Notice);
    }

    [Fact]
    public async Task Confirmed_when_new_process_sets_confirmed()
    {
        var other = new UpdateStateStore(_h.Paths.StateFile);

        var outcome = await Drive(Run(), each: i =>
        {
            if (i == 1)
            {
                other.Update(s => s.Pending = _pending with { Confirmed = true });
            }
        });

        Assert.Equal(HandoverOutcome.Confirmed, outcome);
        Assert.Single(_starter.Paths);
        Assert.False(_starter.Started[0].Killed);
        Assert.True(_starter.Started[0].Disposed);
        Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.OldExePath));
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Confirmed_when_pending_is_cleared()
    {
        var outcome = await Drive(Run(), each: i =>
        {
            if (i == 2)
            {
                _h.Store.Update(s => s.Pending = null);
            }
        });

        Assert.Equal(HandoverOutcome.Confirmed, outcome);
        Assert.Single(_starter.Paths);
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Unreadable_state_means_not_yet()
    {
        var good = File.ReadAllText(_h.Paths.StateFile);
        File.WriteAllText(_h.Paths.StateFile, "{");

        var outcome = await Drive(Run(), each: i =>
        {
            if (i == 3)
            {
                File.WriteAllText(_h.Paths.StateFile, good.Replace("\"Confirmed\": false", "\"Confirmed\": true"));
            }
        });

        Assert.Equal(HandoverOutcome.Confirmed, outcome);
        Assert.False(_starter.Started[0].Killed);
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Early_exit_rolls_back()
    {
        var first = new FakeUpdateProcess { HasExited = true };
        _starter.Script(first);

        var outcome = await Drive(Run());

        Assert.Equal(HandoverOutcome.RolledBack, outcome);
        Assert.Equal(2, _starter.Paths.Count);
        Assert.False(first.Killed);
        Assert.True(first.Disposed);
        AssertRolledBack();
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Timeout_kills_and_rolls_back()
    {
        var first = new FakeUpdateProcess();
        _starter.Script(first);

        var outcome = await Drive(Run());

        Assert.Equal(HandoverOutcome.RolledBack, outcome);
        Assert.True(first.Killed);
        Assert.True(first.Disposed);
        Assert.True(_h.Clock.GetUtcNow() >= ManualTimeProvider.T0 + UpdateHandover.ConfirmTimeout);
        Assert.Equal(2, _starter.Paths.Count);
        AssertRolledBack();
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Process_that_ignores_kill_does_not_block_rollback_forever()
    {
        var first = new FakeUpdateProcess { ExitWhenKilled = false };
        _starter.Script(first);

        var outcome = await Drive(Run());

        Assert.Equal(HandoverOutcome.RolledBack, outcome);
        Assert.True(first.Killed);
        AssertRolledBack();
    }

    [Fact]
    public async Task Start_failure_rolls_back_without_kill()
    {
        _starter.ThrowOnCalls.Add(0);

        var outcome = await Drive(Run());

        Assert.Equal(HandoverOutcome.RolledBack, outcome);
        Assert.Equal(2, _starter.Paths.Count);
        Assert.Single(_starter.Started);
        Assert.False(_starter.Started[0].Killed);
        AssertRolledBack();
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    public async Task Missing_old_exe_fails_rollback()
    {
        File.Delete(_h.Paths.OldExePath);
        _starter.Script(new FakeUpdateProcess { HasExited = true });

        var outcome = await Drive(Run());

        Assert.Equal(HandoverOutcome.RollbackFailed, outcome);
        Assert.NotNull(_h.Store.Load().Pending);
        Assert.Single(_starter.Paths);
        AssertEveryStartWasTheInstalledExe();
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Exe_held_open_fails_rollback_after_ten_seconds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        _starter.Script(new FakeUpdateProcess { HasExited = true });
        using var hold = new FileStream(_h.Paths.ExePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = await Drive(Run(), TimeSpan.FromMilliseconds(500));

        Assert.Equal(HandoverOutcome.RollbackFailed, outcome);
        Assert.True(File.Exists(_h.Paths.OldExePath));
        Assert.NotNull(_h.Store.Load().Pending);
        Assert.Single(_starter.Paths);
        AssertEveryStartWasTheInstalledExe();
    }
}

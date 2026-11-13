namespace ClippysRevenge.Core.Tests;

public sealed class UpdateStartupTests : IDisposable
{
    private readonly UpdateHarness _h = new();
    private static readonly SemVersion V07 = SemVersion.Parse("0.0.7");

    public void Dispose() => _h.Dispose();

    private StartupUpdatePlan Prepare(SemVersion running, UpdateMode mode = UpdateMode.Install) =>
        UpdateStartup.Prepare(_h.Paths, _h.Store, running, mode);

    private string Staged(SemVersion v) => _h.Paths.StagedExePath(v);

    [Fact]
    public void Empty_state_gives_nothing()
    {
        File.Delete(_h.Paths.StagedExePath(UpdateHarness.V06));

        var plan = Prepare(UpdateHarness.V05);

        Assert.Null(plan.InstallNow);
        Assert.False(plan.ConfirmAfterStart);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Pending_for_this_version_confirms_and_never_installs()
    {
        var staged = _h.WriteStaged(V07, _h.NewBytes);
        var pending = UpdateHarness.Pending();
        _h.Store.Update(s =>
        {
            s.Pending = pending;
            s.Staged = staged;
        });

        var plan = Prepare(UpdateHarness.V06);

        Assert.True(plan.ConfirmAfterStart);
        Assert.Null(plan.InstallNow);
        Assert.Equal(pending, _h.Store.Load().Pending);
    }

    [Fact]
    public void Pending_for_another_version_is_a_failed_update()
    {
        _h.Store.Update(s => s.Pending = UpdateHarness.Pending());

        var plan = Prepare(UpdateHarness.V05);

        var s = _h.Store.Load();
        Assert.Equal("0.0.6", s.FailedVersion);
        Assert.Null(s.Pending);
        var expected = new UpdateNotice(UpdateNoticeKind.RolledBack, "0.0.6", "0.0.5");
        Assert.Equal(expected, plan.Notice);
        Assert.False(plan.ConfirmAfterStart);
    }

    [Fact]
    public void Stored_notice_is_returned_once()
    {
        var notice = new UpdateNotice(UpdateNoticeKind.Updated, "0.0.4", "0.0.5");
        _h.Store.Update(s => s.Notice = notice);

        Assert.Equal(notice, Prepare(UpdateHarness.V05).Notice);
        Assert.Null(Prepare(UpdateHarness.V05).Notice);
    }

    [Fact]
    public void Old_exe_is_deleted_only_when_exe_exists()
    {
        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        Prepare(UpdateHarness.V05);
        Assert.False(File.Exists(_h.Paths.OldExePath));

        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        File.Delete(_h.Paths.ExePath);
        Prepare(UpdateHarness.V05);
        Assert.True(File.Exists(_h.Paths.OldExePath));
    }

    [Fact]
    public void Staging_folder_is_cleaned()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        var old = _h.WriteStaged(SemVersion.Parse("0.0.4"), _h.NewBytes);
        Assert.NotNull(old);
        var partial = Staged(V07) + ".partial";
        File.WriteAllText(partial, "x");
        var notes = Path.Combine(_h.Paths.StagingDir, "notes.txt");
        File.WriteAllText(notes, "x");
        var sub = Path.Combine(_h.Paths.StagingDir, "sub");
        Directory.CreateDirectory(sub);

        Prepare(UpdateHarness.V05);

        var left = Directory.EnumerateFileSystemEntries(_h.Paths.StagingDir).Order().ToArray();
        Assert.Equal(
            new[] { Staged(UpdateHarness.V06), notes, sub }.Order().ToArray(),
            left);
    }

    [Fact]
    public void Install_now_follows_mode_and_validity()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        Assert.Equal(_h.Staged, Prepare(UpdateHarness.V05).InstallNow);

        Assert.Null(Prepare(UpdateHarness.V05, UpdateMode.Notify).InstallNow);
        Assert.True(File.Exists(Staged(UpdateHarness.V06)));
        Assert.Null(Prepare(UpdateHarness.V05, UpdateMode.Off).InstallNow);
        Assert.True(File.Exists(Staged(UpdateHarness.V06)));
    }

    [Fact]
    public void Failed_version_is_not_installed_and_its_file_is_deleted()
    {
        _h.Store.Update(s =>
        {
            s.Staged = _h.Staged;
            s.FailedVersion = "0.0.6";
        });

        Assert.Null(Prepare(UpdateHarness.V05).InstallNow);
        Assert.False(File.Exists(Staged(UpdateHarness.V06)));
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public void Not_newer_staged_is_dropped()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);

        Assert.Null(Prepare(UpdateHarness.V06).InstallNow);
        Assert.False(File.Exists(Staged(UpdateHarness.V06)));
    }

    [Fact]
    public void Wrong_size_staged_is_dropped()
    {
        _h.Store.Update(s => s.Staged = _h.Staged with { Size = _h.Staged.Size + 1 });

        Assert.Null(Prepare(UpdateHarness.V05).InstallNow);
        Assert.False(File.Exists(Staged(UpdateHarness.V06)));
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public void Missing_staged_file_clears_staged()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        File.Delete(Staged(UpdateHarness.V06));

        Assert.Null(Prepare(UpdateHarness.V05).InstallNow);
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public void Unreadable_state_is_treated_as_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Paths.StateFile)!);
        File.WriteAllText(_h.Paths.StateFile, "{ not json");

        var plan = Prepare(UpdateHarness.V05);

        Assert.Null(plan.InstallNow);
        Assert.False(plan.ConfirmAfterStart);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Confirm_marks_pending_once()
    {
        _h.Store.Update(s => s.Pending = UpdateHarness.Pending());

        var notice = UpdateStartup.Confirm(_h.Store, UpdateHarness.V06);

        Assert.Equal(new UpdateNotice(UpdateNoticeKind.Updated, "0.0.5", "0.0.6"), notice);
        Assert.True(_h.Store.Load().Pending!.Confirmed);
        Assert.Null(UpdateStartup.Confirm(_h.Store, UpdateHarness.V06));
    }

    [Fact]
    public void Confirm_ignores_missing_or_other_pending()
    {
        Assert.Null(UpdateStartup.Confirm(_h.Store, UpdateHarness.V06));

        var pending = UpdateHarness.Pending();
        _h.Store.Update(s => s.Pending = pending);
        Assert.Null(UpdateStartup.Confirm(_h.Store, V07));
        Assert.Equal(pending, _h.Store.Load().Pending);
    }

    private Task Cleanup() =>
        UpdateStartup.CleanupOldAsync(_h.Paths, _h.Store, UpdateHarness.V06, _h.Clock);

    [Fact]
    public async Task Cleanup_deletes_old_and_clears_confirmed_pending()
    {
        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        _h.Store.Update(s => s.Pending = UpdateHarness.Pending(confirmed: true));

        await Cleanup();

        Assert.False(File.Exists(_h.Paths.OldExePath));
        Assert.Null(_h.Store.Load().Pending);
        Assert.True(File.Exists(_h.Paths.ExePath));
    }

    [Fact]
    public async Task Cleanup_keeps_unconfirmed_or_foreign_pending()
    {
        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        var unconfirmed = UpdateHarness.Pending();
        _h.Store.Update(s => s.Pending = unconfirmed);
        await Cleanup();
        Assert.Equal(unconfirmed, _h.Store.Load().Pending);

        var foreign = new PendingInstall("0.0.6", "0.0.7", ManualTimeProvider.T0, true);
        _h.Store.Update(s => s.Pending = foreign);
        await Cleanup();
        Assert.Equal(foreign, _h.Store.Load().Pending);
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Cleanup_retries_until_the_old_exe_is_released()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        _h.Store.Update(s => s.Pending = UpdateHarness.Pending(confirmed: true));
        var hold = new FileStream(_h.Paths.OldExePath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var released = false;
            await _h.Drive(Cleanup(), TimeSpan.FromSeconds(1), i =>
            {
                if (i == 3 && !released)
                {
                    released = true;
                    hold.Dispose();
                }
            });
        }
        finally
        {
            hold.Dispose();
        }

        Assert.False(File.Exists(_h.Paths.OldExePath));
        Assert.Null(_h.Store.Load().Pending);
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Cleanup_gives_up_after_sixty_seconds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllBytes(_h.Paths.OldExePath, UpdateHarness.OldBytes);
        var pending = UpdateHarness.Pending(confirmed: true);
        _h.Store.Update(s => s.Pending = pending);
        using var hold = new FileStream(_h.Paths.OldExePath, FileMode.Open, FileAccess.Read, FileShare.None);

        await _h.Drive(Cleanup(), TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(_h.Paths.OldExePath));
        Assert.Equal(pending, _h.Store.Load().Pending);
    }

    [Fact]
    public async Task Full_flow_round_trip_confirms()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        var pending = await UpdateInstall.BeginAsync(_h.Paths, _h.Store, _h.Staged, UpdateHarness.V05, _h.Clock,
            null, TestContext.Current.CancellationToken);
        var starter = new FakeUpdateStarter
        {
            OnStart = _ =>
            {
                // What the new process does at start-up.
                var plan = UpdateStartup.Prepare(_h.Paths, _h.Store, UpdateHarness.V06, UpdateMode.Install);
                Assert.True(plan.ConfirmAfterStart);
                Assert.NotNull(UpdateStartup.Confirm(_h.Store, UpdateHarness.V06));
                UpdateStartup.CleanupOldAsync(_h.Paths, _h.Store, UpdateHarness.V06, _h.Clock)
                    .GetAwaiter().GetResult();
            },
        };

        var outcome = await UpdateHandover.RunAsync(_h.Paths, _h.Store, pending, starter.Start, _h.Clock, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HandoverOutcome.Confirmed, outcome);
        Assert.False(File.Exists(_h.Paths.OldExePath));
        Assert.Null(_h.Store.Load().Pending);
        Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.ExePath));
    }
}

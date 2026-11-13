namespace ClippysRevenge.Core.Tests;

public sealed class UpdateInstallTests : IDisposable
{
    private readonly UpdateHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<PendingInstall> Begin(StagedUpdate? staged = null, UpdateStateStore? store = null,
        CancellationToken? ct = null) =>
        UpdateInstall.BeginAsync(_h.Paths, store ?? _h.Store, staged ?? _h.Staged, UpdateHarness.V05, _h.Clock,
            null, ct ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task Happy_path_swaps_files_and_writes_pending()
    {
        _h.Store.Update(s =>
        {
            s.Staged = _h.Staged;
            s.LastError = "old error";
        });

        var pending = await Begin();

        Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.OldExePath));
        Assert.False(File.Exists(_h.Paths.StagedExePath(UpdateHarness.V06)));
        var s = _h.Store.Load();
        Assert.Equal(new PendingInstall("0.0.5", "0.0.6", ManualTimeProvider.T0, false), s.Pending);
        Assert.Equal(pending, s.Pending);
        Assert.Null(s.Staged);
        Assert.Null(s.LastError);
    }

    [Fact]
    public async Task Leftover_old_is_deleted_first()
    {
        File.WriteAllBytes(_h.Paths.OldExePath, "leftover"u8.ToArray());

        await Begin();

        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.OldExePath));
        Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.ExePath));
    }

    [Fact]
    public async Task Missing_staged_file_clears_staged()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        File.Delete(_h.Paths.StagedExePath(UpdateHarness.V06));

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() => Begin());

        Assert.Equal(UpdateTexts.ReasonStagedMissing, ex.Message);
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public async Task Wrong_size_deletes_staged_file()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        var wrong = _h.Staged with { Size = _h.Staged.Size + 1 };

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() => Begin(wrong));

        Assert.Equal(UpdateTexts.ReasonSizeMismatch, ex.Message);
        Assert.False(File.Exists(_h.Paths.StagedExePath(UpdateHarness.V06)));
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public async Task Wrong_hash_deletes_staged_file()
    {
        _h.Store.Update(s => s.Staged = _h.Staged);
        var flipped = (byte[])_h.NewBytes.Clone();
        flipped[100] ^= 0xFF;
        File.WriteAllBytes(_h.Paths.StagedExePath(UpdateHarness.V06), flipped);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() => Begin());

        Assert.Equal(UpdateTexts.ReasonHashMismatch, ex.Message);
        Assert.False(File.Exists(_h.Paths.StagedExePath(UpdateHarness.V06)));
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.Null(_h.Store.Load().Staged);
    }

    [Fact]
    public async Task Missing_exe_fails_with_replace_reason()
    {
        File.Delete(_h.Paths.ExePath);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() => Begin());

        Assert.Equal(UpdateTexts.ReasonReplaceFailed("FileNotFoundException"), ex.Message);
        Assert.True(File.Exists(_h.Paths.StagedExePath(UpdateHarness.V06)));
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Old_in_use_fails_after_retries()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllBytes(_h.Paths.OldExePath, "busy"u8.ToArray());
        using var hold = new FileStream(_h.Paths.OldExePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() =>
            _h.Drive(Begin(), TimeSpan.FromMilliseconds(500)));

        Assert.Equal(UpdateTexts.ReasonOldInUse, ex.Message);
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Failed_move_restores_old_exe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var src = _h.Paths.StagedExePath(UpdateHarness.V06);
        // Share Read only: hashing can open it, but renaming it is refused.
        var task = Begin();
        using var hold = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() =>
            _h.Drive(task, TimeSpan.FromMilliseconds(500)));

        Assert.StartsWith("the installed program couldn't be replaced", ex.Message);
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.False(File.Exists(_h.Paths.OldExePath));
    }

    [Fact]
    public async Task Unwritable_store_undoes_the_swap()
    {
        var dirStore = Path.Combine(_h.Root, "state-is-a-dir");
        Directory.CreateDirectory(dirStore);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(() =>
            _h.Drive(Begin(store: new UpdateStateStore(dirStore)), TimeSpan.FromMilliseconds(500)));

        Assert.StartsWith("the installed program couldn't be replaced", ex.Message);
        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.False(File.Exists(_h.Paths.OldExePath));
        Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.StagedExePath(UpdateHarness.V06)));
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Pending_write_failing_twice_is_retried_without_undoing_the_swap()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        // The state writer's temp file is locked, so the first writes fail; it is released after two retries.
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Store.Path)!);
        var hold = new FileStream(_h.Store.Path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var pending = await _h.Drive(Begin(), TimeSpan.FromMilliseconds(250), i =>
            {
                if (i == 2)
                {
                    hold.Dispose();
                }
            });

            Assert.Equal(_h.NewBytes, File.ReadAllBytes(_h.Paths.ExePath));
            Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.OldExePath));
            Assert.Equal(pending, _h.Store.Load().Pending);
        }
        finally
        {
            hold.Dispose();
        }
    }

    [Fact]
    public async Task Cancelled_before_call_changes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Begin(ct: cts.Token));

        Assert.Equal(UpdateHarness.OldBytes, File.ReadAllBytes(_h.Paths.ExePath));
        Assert.False(File.Exists(_h.Paths.OldExePath));
        Assert.True(File.Exists(_h.Paths.StagedExePath(UpdateHarness.V06)));
    }
}

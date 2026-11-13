using System.Security.Cryptography;

namespace ClippysRevenge.Core.Tests;

/// <summary>Scriptable <see cref="IUpdateProcess"/>.</summary>
internal sealed class FakeUpdateProcess : IUpdateProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasExited;

    public bool HasExited
    {
        get => Volatile.Read(ref _hasExited);
        set
        {
            Volatile.Write(ref _hasExited, value);
            if (value)
            {
                _exited.TrySetResult();
            }
        }
    }

    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public bool ExitWhenKilled { get; set; } = true;

    public void Kill()
    {
        Killed = true;
        if (ExitWhenKilled)
        {
            HasExited = true;
        }
    }

    public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

    public void Dispose() => Disposed = true;
}

/// <summary>The "start" callback: records each path, hands out scripted processes, can throw.</summary>
internal sealed class FakeUpdateStarter
{
    private readonly Queue<FakeUpdateProcess> _scripted = new();

    public List<string> Paths { get; } = [];
    public List<FakeUpdateProcess> Started { get; } = [];
    public HashSet<int> ThrowOnCalls { get; } = [];

    /// <summary>Runs on each call, before the process is returned (0-based call index).</summary>
    public Action<int>? OnStart { get; set; }

    public void Script(FakeUpdateProcess p) => _scripted.Enqueue(p);

    public IUpdateProcess Start(string path)
    {
        var index = Paths.Count;
        Paths.Add(path);
        OnStart?.Invoke(index);
        if (ThrowOnCalls.Contains(index))
        {
            throw new InvalidOperationException("start failed");
        }
        var p = _scripted.Count > 0 ? _scripted.Dequeue() : new FakeUpdateProcess();
        Started.Add(p);
        return p;
    }
}

/// <summary>Temp install folder with an old exe, a staged 0.0.6 exe and a state store. Deleted on dispose.</summary>
internal sealed class UpdateHarness : IDisposable
{
    public static readonly SemVersion V05 = SemVersion.Parse("0.0.5");
    public static readonly SemVersion V06 = SemVersion.Parse("0.0.6");
    public static readonly byte[] OldBytes = "MZold"u8.ToArray();

    public UpdateHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "clippy-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = UpdatePaths.Default(Root);
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.StagingDir);
        File.WriteAllBytes(Paths.ExePath, OldBytes);
        NewBytes = MakeNewBytes();
        Staged = WriteStaged(V06, NewBytes);
        Store = new UpdateStateStore(Paths.StateFile);
        Clock = new ManualTimeProvider();
    }

    public string Root { get; }
    public UpdatePaths Paths { get; }
    public UpdateStateStore Store { get; }
    public ManualTimeProvider Clock { get; }
    public byte[] NewBytes { get; }
    public StagedUpdate Staged { get; }

    public static byte[] MakeNewBytes()
    {
        var bytes = new byte[2 * 1024 * 1024 + 5];
        "MZnew"u8.CopyTo(bytes);
        return bytes;
    }

    public StagedUpdate WriteStaged(SemVersion v, byte[] bytes)
    {
        File.WriteAllBytes(Paths.StagedExePath(v), bytes);
        return new StagedUpdate(v.ToString(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    public static PendingInstall Pending(bool confirmed = false) =>
        new("0.0.5", "0.0.6", ManualTimeProvider.T0, confirmed);

    /// <summary>Awaits the task while advancing the fake clock in <paramref name="step"/> slices.</summary>
    public async Task<T> Drive<T>(Task<T> task, TimeSpan step, Action<int>? perStep = null, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            perStep?.Invoke(i);
            Clock.Advance(step);
            await Task.Delay(2);
        }
        Assert.True(task.IsCompleted, "task did not complete");
        return await task;
    }

    public async Task Drive(Task task, TimeSpan step, Action<int>? perStep = null, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            perStep?.Invoke(i);
            Clock.Advance(step);
            await Task.Delay(2);
        }
        Assert.True(task.IsCompleted, "task did not complete");
        await task;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

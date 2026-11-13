using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class SingleInstanceTests
{
    private static string M() => @"Local\ClippyTest-" + Guid.NewGuid().ToString("N");
    private static string P() => "ClippyTest-" + Guid.NewGuid().ToString("N");

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return cond();
    }

    [Fact]
    public void Acquire_NotNull()
    {
        using var a = SingleInstance.TryAcquire(M(), P());
        Assert.NotNull(a);
    }

    [Fact]
    public void SecondAcquire_Null_ThenAgainAfterDispose()
    {
        var m = M();
        var p = P();
        var a = SingleInstance.TryAcquire(m, p);
        Assert.NotNull(a);
        Assert.Null(SingleInstance.TryAcquire(m, p));
        a.Dispose();
        using var b = SingleInstance.TryAcquire(m, p);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Signal_InvokesCallback()
    {
        var p = P();
        using var a = SingleInstance.TryAcquire(M(), p)!;
        var count = 0;
        a.StartListening(() => Interlocked.Increment(ref count));
        Assert.True(SingleInstance.SignalExisting("show-settings", p));
        Assert.True(await WaitFor(() => Volatile.Read(ref count) == 1));
    }

    [Fact]
    public async Task ThreeSignals_ThreeCallbacks()
    {
        var p = P();
        using var a = SingleInstance.TryAcquire(M(), p)!;
        var count = 0;
        a.StartListening(() => Interlocked.Increment(ref count));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(SingleInstance.SignalExisting("show-settings", p));
            Assert.True(await WaitFor(() => Volatile.Read(ref count) == i + 1));
        }
    }

    [Fact]
    public async Task UnknownCommand_Ignored()
    {
        var p = P();
        using var a = SingleInstance.TryAcquire(M(), p)!;
        var count = 0;
        a.StartListening(() => Interlocked.Increment(ref count));
        Assert.True(SingleInstance.SignalExisting("delete-everything", p));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref count));
        Assert.True(SingleInstance.SignalExisting("show-settings", p));
        Assert.True(await WaitFor(() => Volatile.Read(ref count) == 1));
    }

    [Fact]
    public async Task OverlongLine_Ignored()
    {
        var p = P();
        using var a = SingleInstance.TryAcquire(M(), p)!;
        var count = 0;
        a.StartListening(() => Interlocked.Increment(ref count));
        using (var c = new NamedPipeClientStream(".", p, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            c.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(new string('x', 10000));
            try
            {
                c.Write(bytes, 0, bytes.Length);
                c.Flush();
            }
            catch (IOException)
            {
                // Server may close early.
            }
        }
        Assert.Equal(0, Volatile.Read(ref count));
        Assert.True(await WaitFor(() => SingleInstance.SignalExisting("show-settings", p, 500) && Volatile.Read(ref count) >= 1));
    }

    [Fact]
    public async Task ThrowingCallback_LoopSurvives()
    {
        var p = P();
        using var a = SingleInstance.TryAcquire(M(), p)!;
        var count = 0;
        a.StartListening(() =>
        {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("boom");
        });
        Assert.True(SingleInstance.SignalExisting("show-settings", p));
        Assert.True(await WaitFor(() => Volatile.Read(ref count) == 1));
        Assert.True(SingleInstance.SignalExisting("show-settings", p));
        Assert.True(await WaitFor(() => Volatile.Read(ref count) == 2));
    }

    [Fact]
    public void Signal_NoServer_FalseQuickly()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(SingleInstance.SignalExisting("show-settings", P(), 200));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Signal_AfterDispose_False()
    {
        var p = P();
        var a = SingleInstance.TryAcquire(M(), p)!;
        a.StartListening(() => { });
        a.Dispose();
        Assert.False(SingleInstance.SignalExisting("show-settings", p, 200));
    }

    [Fact]
    public void StartListening_Twice_Throws()
    {
        using var a = SingleInstance.TryAcquire(M(), P())!;
        a.StartListening(() => { });
        Assert.Throws<InvalidOperationException>(() => a.StartListening(() => { }));
    }

    [Fact]
    public void DefaultPipeName_HasSessionId()
    {
        Assert.StartsWith("ClippysRevenge-", SingleInstance.DefaultPipeName);
        Assert.EndsWith(Process.GetCurrentProcess().SessionId.ToString(), SingleInstance.DefaultPipeName);
    }

    [Fact]
    public void Dispose_Twice_NoThrow()
    {
        var a = SingleInstance.TryAcquire(M(), P())!;
        a.Dispose();
        a.Dispose();
    }
}

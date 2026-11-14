using System.Diagnostics;
using System.Runtime.InteropServices;
using ClippysRevenge.App.Tray;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

[CollectionDefinition("GdiHandleCount", DisableParallelization = true)]
public class GdiHandleCountCollection;

/// <summary>
/// Process-wide GDI/USER counts: runs alone so parallel test classes don't disturb them.
/// </summary>
[Collection("GdiHandleCount")]
[Trait("os", "windows")]
public sealed class TrayBadgeHandleTests
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint hProcess, uint flags);

    private static (uint Gdi, uint User) Counts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        return (GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void No_handle_leak_over_1000_swaps(int px)
    {
        var (g0, u0) = Counts();
        var r = new TrayBadgeRenderer(px);
        void Swap(int i)
        {
            var ic = r.Get(TrayIconKey.Idle + i % 10, AttentionBadge.Text(1 + i % 11));
            _ = ic.Handle;
        }
        for (var i = 0; i < 50; i++) Swap(i);
        var (g1, u1) = Counts();
        for (var i = 0; i < 1000; i++) Swap(i);
        var (g2, u2) = Counts();
        Assert.True(r.HeldIcons <= 20, $"Held icons {r.HeldIcons}");
        Assert.True(g2 - g1 <= 20, $"GDI objects grew by {(long)g2 - g1}");
        Assert.True(u2 - u1 <= 20, $"USER objects grew by {(long)u2 - u1}");
        r.Dispose();
        var (g3, u3) = Counts();
        Assert.True(g3 <= g0 + 4, $"GDI after dispose {g3} vs start {g0}");
        Assert.True(u3 <= u0 + 4, $"USER after dispose {u3} vs start {u0}");
    }
}
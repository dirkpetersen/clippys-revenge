using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ClippysRevenge.App.Tray;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Badged tray icons (SPEC 22.6): GDI+ layer pixels and handle hygiene. Windows only, no Outlook needed.</summary>
[Trait("os", "windows")]
public sealed class TrayBadgeRendererTests
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint hProcess, uint flags);

    private static (byte R, byte G, byte B, byte A) Px(byte[] rgba, int px, int x, int y)
    {
        var i = (y * px + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    private static bool IsDark((byte R, byte G, byte B, byte A) p) => p.A == 255 && p.R <= 0x40 && p.G <= 0x40 && p.B <= 0x40;

    private static bool IsFill((byte R, byte G, byte B, byte A) p) => p == (0xFF, 0xD4, 0x00, 0xFF);

    public static IEnumerable<object[]> SizesAndTexts() =>
        from px in TrayIconArt.Sizes
        from text in new[] { "1", "9+" }
        select new object[] { px, text };

    [Theory]
    [MemberData(nameof(SizesAndTexts))]
    public void Layer_only_inside_badge(int px, string text)
    {
        var l = AttentionBadge.Layout(px, text);
        var layer = TrayBadgeRenderer.RenderLayer(px, text);
        Assert.Equal(px * px * 4, layer.Length);
        var inBadge = false;
        var inRing = false;
        var inDigit = false;
        var fillCounts = new Dictionary<(byte, byte, byte, byte), int>();
        for (var y = 0; y < px; y++)
        {
            for (var x = 0; x < px; x++)
            {
                var p = Px(layer, px, x, y);
                if (!l.Badge.Contains(x, y))
                {
                    Assert.Equal(0, p.A);
                    continue;
                }
                inBadge |= p.A > 0;
                if (l.Inner.Contains(x, y) && p.A == 255)
                {
                    fillCounts[p] = fillCounts.GetValueOrDefault(p) + 1;
                }
                if (IsDark(p) && l.TextBox.Contains(x, y)) inDigit = true;
                if (IsDark(p) && !l.Inner.Contains(x, y)) inRing = true;
            }
        }
        Assert.True(inBadge);
        Assert.Equal((0xFF, 0xD4, 0x00, 0xFF), fillCounts.MaxBy(kv => kv.Value).Key);
        Assert.True(inDigit, "a dark digit pixel in TextBox");
        Assert.True(inRing, "a dark outline pixel outside Inner");
    }

    [Fact]
    public void Compose_keeps_base_outside_badge()
    {
        foreach (var key in Enum.GetValues<TrayIconKey>())
        {
            foreach (var px in TrayIconArt.Sizes)
            {
                foreach (var text in new[] { "1", "9+" })
                {
                    var l = AttentionBadge.Layout(px, text);
                    var baseRgba = TrayIconArt.RenderRgba(key, px);
                    var r = TrayBadgeRenderer.ComposeRgba(key, px, text);
                    var fill = 0;
                    for (var y = 0; y < px; y++)
                    {
                        for (var x = 0; x < px; x++)
                        {
                            var i = (y * px + x) * 4;
                            if (!l.Badge.Contains(x, y))
                            {
                                Assert.Equal(Px(baseRgba, px, x, y), Px(r, px, x, y));
                            }
                            else if (l.Inner.Contains(x, y) && IsFill(Px(r, px, x, y)))
                            {
                                fill++;
                            }
                        }
                    }
                    Assert.True(fill * 4 >= l.Inner.Width * l.Inner.Height, $"{key} {px} {text}: fill {fill}");
                    // One digit: around the centre. "9+": two glyphs with a gap in the middle, so anywhere in TextBox.
                    var box = text.Length == 1
                        ? new PixelRect(l.TextBox.X + l.TextBox.Width / 2 - 1, l.TextBox.Y + l.TextBox.Height / 2 - 1, 3, 3)
                        : l.TextBox;
                    var dark = false;
                    for (var y = box.Y; y < box.Bottom; y++)
                        for (var x = box.X; x < box.Right; x++)
                            dark |= IsDark(Px(r, px, x, y));
                    Assert.True(dark, $"{key} {px} {text}: no dark digit pixel in the check area");
                }
            }
        }
    }

    [Fact]
    public void Badge_differs_per_text()
    {
        var one = TrayBadgeRenderer.ComposeRgba(TrayIconKey.Idle, 16, "1");
        var two = TrayBadgeRenderer.ComposeRgba(TrayIconKey.Idle, 16, "2");
        var nine = TrayBadgeRenderer.ComposeRgba(TrayIconKey.Idle, 16, "9+");
        Assert.NotEqual(one, two);
        Assert.NotEqual(two, nine);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Get_returns_icon_of_size(int px)
    {
        using var r = new TrayBadgeRenderer(px);
        var icon = r.Get(TrayIconKey.Idle, "3");
        Assert.Equal(px, icon.Width);
        Assert.Equal(px, icon.Height);
        Assert.NotEqual((nint)0, icon.Handle);
    }

    [Fact]
    public void Get_caches_and_retires()
    {
        using var r = new TrayBadgeRenderer(16);
        var a = r.Get(TrayIconKey.Idle, "1");
        Assert.Same(a, r.Get(TrayIconKey.Idle, "1"));
        Assert.NotSame(a, r.Get(TrayIconKey.Busy, "1"));
        Assert.True(r.HeldIcons <= 20);
        r.Get(TrayIconKey.Idle, "2");
        _ = a.Handle;
        Assert.True(r.HeldIcons <= 20);
        r.Get(TrayIconKey.Idle, "3");
        Assert.Throws<ObjectDisposedException>(() => a.Handle);
        Assert.True(r.HeldIcons <= 20);
    }

    [Fact]
    public void Same_text_no_rebuild()
    {
        using var r = new TrayBadgeRenderer(16);
        var a = r.Get(TrayIconKey.Idle, AttentionBadge.Text(10));
        var b = r.Get(TrayIconKey.Idle, AttentionBadge.Text(11));
        Assert.Same(a, b);
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrayBadgeRenderer(28));
        var r = new TrayBadgeRenderer(16);
        Assert.Throws<ArgumentException>(() => r.Get(TrayIconKey.Idle, ""));
        var icon = r.Get(TrayIconKey.Idle, "1");
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.Get(TrayIconKey.Idle, "1"));
        Assert.Throws<ObjectDisposedException>(() => icon.Handle);
        r.Dispose();
    }

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
        Assert.True(g2 - g1 <= 20, $"GDI objects grew by {(long)g2 - g1}");
        Assert.True(u2 - u1 <= 20, $"USER objects grew by {(long)u2 - u1}");
        r.Dispose();
        var (g3, u3) = Counts();
        Assert.True(g3 <= g0 + 4, $"GDI after dispose {g3} vs start {g0}");
        Assert.True(u3 <= u0 + 4, $"USER after dispose {u3} vs start {u0}");
    }
}

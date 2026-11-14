using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AttentionBadgeTests
{
    private static readonly string S1 = AttentionBadge.TooltipSuffix(1);
    private static readonly string S2 = AttentionBadge.TooltipSuffix(2);

    [Fact]
    public void Text_values()
    {
        Assert.Equal("", AttentionBadge.Text(0));
        Assert.Equal("1", AttentionBadge.Text(1));
        Assert.Equal("9", AttentionBadge.Text(9));
        Assert.Equal("9+", AttentionBadge.Text(10));
        Assert.Equal("9+", AttentionBadge.Text(250));
        Assert.Equal("9+", AttentionBadge.Text(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Text(-1));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 20)]
    [InlineData(20, 20)]
    [InlineData(21, 24)]
    [InlineData(24, 24)]
    [InlineData(25, 32)]
    [InlineData(28, 32)]
    [InlineData(32, 32)]
    [InlineData(48, 32)]
    public void PickIconSize_values(int requested, int expected) =>
        Assert.Equal(expected, AttentionBadge.PickIconSize(requested));

    [Fact]
    public void PickIconSize_zero_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.PickIconSize(0));

    [Theory]
    [InlineData(16, "1", 6, 0, 10, 10, 1, 9)]
    [InlineData(16, "9+", 1, 0, 15, 10, 1, 9)]
    [InlineData(20, "1", 8, 0, 12, 12, 1, 11)]
    [InlineData(20, "9+", 2, 0, 18, 12, 1, 11)]
    [InlineData(24, "1", 10, 0, 14, 14, 1, 13)]
    [InlineData(24, "9+", 3, 0, 21, 14, 1, 13)]
    [InlineData(32, "1", 13, 0, 19, 19, 2, 17)]
    [InlineData(32, "9+", 4, 0, 28, 19, 2, 17)]
    public void Layout_exact(int px, string text, int x, int y, int w, int h, int outline, int font)
    {
        var l = AttentionBadge.Layout(px, text);
        Assert.Equal(px, l.IconPx);
        Assert.Equal(new PixelRect(x, y, w, h), l.Badge);
        Assert.Equal(outline, l.Outline);
        Assert.Equal(font, l.FontPx);
    }

    [Fact]
    public void Layout_worked_examples()
    {
        var a = AttentionBadge.Layout(16, "1");
        Assert.Equal(new PixelRect(7, 1, 8, 8), a.Inner);
        Assert.Equal(new PixelRect(8, 2, 6, 6), a.TextBox);
        var b = AttentionBadge.Layout(32, "9+");
        Assert.Equal(new PixelRect(6, 2, 24, 15), b.Inner);
        Assert.Equal(new PixelRect(7, 3, 22, 13), b.TextBox);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Layout_invariants(int px)
    {
        var narrow = AttentionBadge.Layout(px, "1");
        var wide = AttentionBadge.Layout(px, "9+");
        foreach (var l in new[] { narrow, wide })
        {
            Assert.Equal(px, l.Badge.Right);
            Assert.Equal(0, l.Badge.Y);   // top-right on every state
            Assert.True(l.Badge.X >= 0);
            Assert.True(l.Badge.Height >= 9);
            Assert.Equal((int)Math.Round(0.6 * px), l.Badge.Height);
            Assert.Equal(l.Badge.Inset(l.Outline), l.Inner);
            Assert.Equal(l.Inner.Inset(1), l.TextBox);
            Assert.True(l.TextBox.Width > 0);
            Assert.True(l.FontPx < l.Badge.Height);
            Assert.Equal(px == 32 ? 2 : 1, l.Outline);
        }
        Assert.Equal(narrow.Badge.Height, wide.Badge.Height);
        Assert.True(wide.Badge.Width > narrow.Badge.Width);
    }

    [Fact]
    public void Layout_errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Layout(15, "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Layout(28, "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Layout(64, "1"));
        Assert.Throws<ArgumentNullException>(() => AttentionBadge.Layout(16, null!));
        Assert.Throws<ArgumentException>(() => AttentionBadge.Layout(16, ""));
        Assert.Throws<ArgumentException>(() => AttentionBadge.Layout(16, "123"));
        AttentionBadge.Layout(16, "7");
        AttentionBadge.Layout(16, "9+");
    }

    private static byte[] Fillbuf(int px, byte r, byte g, byte b, byte a)
    {
        var buf = new byte[px * px * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = r;
            buf[i + 1] = g;
            buf[i + 2] = b;
            buf[i + 3] = a;
        }
        return buf;
    }

    [Fact]
    public void Composite_outside_area_is_base()
    {
        var baseRgba = Fillbuf(16, 10, 20, 30, 128);
        var layer = Fillbuf(16, 255, 0, 0, 255);
        var baseCopy = (byte[])baseRgba.Clone();
        var layerCopy = (byte[])layer.Clone();
        var area = new PixelRect(6, 6, 10, 10);
        var r = AttentionBadge.Composite(baseRgba, layer, 16, area);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var i = (y * 16 + x) * 4;
                var px = (r[i], r[i + 1], r[i + 2], r[i + 3]);
                Assert.Equal(area.Contains(x, y) ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)10, (byte)20, (byte)30, (byte)128), px);
            }
        }
        Assert.Equal(baseCopy, baseRgba);
        Assert.Equal(layerCopy, layer);
    }

    [Fact]
    public void Composite_zero_alpha_copies_base()
    {
        var baseRgba = new byte[16 * 16 * 4];
        for (var i = 0; i < baseRgba.Length; i++) baseRgba[i] = (byte)(i * 7);
        var r = AttentionBadge.Composite(baseRgba, new byte[baseRgba.Length], 16, new PixelRect(0, 0, 16, 16));
        Assert.Equal(baseRgba, r);
        Assert.NotSame(baseRgba, r);
    }

    private static (int R, int G, int B, int A) One(byte[] baseRgba, byte[] layer)
    {
        var r = AttentionBadge.Composite(baseRgba, layer, 1, new PixelRect(0, 0, 1, 1));
        return (r[0], r[1], r[2], r[3]);
    }

    [Fact]
    public void Composite_blends()
    {
        var (r, g, b, a) = One([255, 255, 255, 255], [0, 0, 0, 128]);
        Assert.InRange(r, 126, 128);
        Assert.InRange(g, 126, 128);
        Assert.InRange(b, 126, 128);
        Assert.Equal(255, a);
        Assert.Equal((0, 0, 0, 128), One([0, 0, 0, 0], [0, 0, 0, 128]));
        Assert.Equal((255, 212, 0, 255), One([9, 8, 7, 99], [255, 212, 0, 255]));
        Assert.Equal((255, 212, 0, 255), One([0, 0, 0, 0], [255, 212, 0, 255]));
    }

    [Fact]
    public void Composite_errors()
    {
        var ok = new byte[16 * 16 * 4];
        var area = new PixelRect(0, 0, 4, 4);
        Assert.Throws<ArgumentNullException>(() => AttentionBadge.Composite(null!, ok, 16, area));
        Assert.Throws<ArgumentNullException>(() => AttentionBadge.Composite(ok, null!, 16, area));
        Assert.Throws<ArgumentException>(() => AttentionBadge.Composite(new byte[16 * 16 * 4 - 1], ok, 16, area));
        Assert.Throws<ArgumentException>(() => AttentionBadge.Composite(ok, new byte[16 * 16 * 4 - 1], 16, area));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Composite(ok, ok, 16, new PixelRect(10, 10, 10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Composite(ok, ok, 16, new PixelRect(-1, 0, 4, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.Composite(ok, ok, 0, area));
    }

    [Fact]
    public void TooltipSuffix_values()
    {
        Assert.Equal("", AttentionBadge.TooltipSuffix(0));
        Assert.Equal(" · 1 draft invite needs your attention", AttentionBadge.TooltipSuffix(1));
        Assert.Equal(" · 2 draft invites need your attention", AttentionBadge.TooltipSuffix(2));
        Assert.Equal(" · 1,240 draft invites need your attention", AttentionBadge.TooltipSuffix(1240));
        Assert.Equal(38, S1.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => AttentionBadge.TooltipSuffix(-1));
    }

    [Fact]
    public void Colours()
    {
        Assert.Equal(((byte)0xFF, (byte)0xD4, (byte)0x00), AttentionBadge.Fill);
        Assert.Equal(((byte)0x1A, (byte)0x1A, (byte)0x1A), AttentionBadge.Ink);
    }

    [Fact]
    public void AppendSuffix_cases()
    {
        Assert.Equal("abc" + S2, TrayText.AppendSuffix("abc", S2));
        Assert.Equal("abc", TrayText.AppendSuffix("abc", ""));

        var long1 = TrayText.AppendSuffix(new string('a', 120), S2);
        Assert.Equal(127, long1.Length);
        Assert.EndsWith(S2, long1);
        Assert.Equal('…', long1[long1.Length - S2.Length - 1]);

        var fits = TrayText.AppendSuffix(new string('a', 127 - S2.Length), S2);
        Assert.Equal(127, fits.Length);
        Assert.DoesNotContain('…', fits);

        var plain = TrayText.AppendSuffix(new string('a', 200), "");
        Assert.Equal(127, plain.Length);
        Assert.EndsWith("…", plain);

        Assert.Throws<ArgumentException>(() => TrayText.AppendSuffix("abc", new string('x', 126)));
        Assert.Throws<ArgumentNullException>(() => TrayText.AppendSuffix(null!, S2));
        Assert.Throws<ArgumentNullException>(() => TrayText.AppendSuffix("abc", null!));
    }
}

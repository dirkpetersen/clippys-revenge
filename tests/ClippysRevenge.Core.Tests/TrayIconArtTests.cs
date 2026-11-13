namespace ClippysRevenge.Core.Tests;

public class TrayIconArtTests
{
    public static IEnumerable<TheoryDataRow<TrayIconKey, int>> KeysAndSizes()
    {
        foreach (var k in Enum.GetValues<TrayIconKey>())
        {
            foreach (var s in TrayIconArt.Sizes)
            {
                yield return new(k, s);
            }
        }
    }

    private static (byte R, byte G, byte B, byte A) Px(byte[] rgba, int size, int x, int y)
    {
        var i = (y * size + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Theory]
    [MemberData(nameof(KeysAndSizes))]
    public void BufferSize(TrayIconKey key, int size)
    {
        var b = TrayIconArt.RenderRgba(key, size);
        Assert.Equal(size * size * 4, b.Length);
        Assert.Contains(Enumerable.Range(0, size * size), i => b[i * 4 + 3] > 0);
    }

    [Fact]
    public void BadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconArt.RenderRgba(TrayIconKey.Idle, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconArt.RenderRgba(TrayIconKey.Idle, 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconArt.RenderRgba((TrayIconKey)99, 16));
    }

    [Theory]
    [InlineData(TrayIconKey.Idle, 0x2E, 0x7D, 0x32)]
    [InlineData(TrayIconKey.Serving, 0x66, 0xBB, 0x6A)]
    [InlineData(TrayIconKey.Busy, 0x15, 0x65, 0xC0)]
    [InlineData(TrayIconKey.Paused, 0x75, 0x75, 0x75)]
    [InlineData(TrayIconKey.Confirm, 0xFF, 0xB3, 0x00)]
    [InlineData(TrayIconKey.Error, 0xC6, 0x28, 0x28)]
    public void ClipColor(TrayIconKey key, int r, int g, int b)
    {
        var p = Px(TrayIconArt.RenderRgba(key, 32), 32, 7, 15);
        Assert.Equal(((byte)r, (byte)g, (byte)b, (byte)255), p);
    }

    [Fact]
    public void TransparentCorner()
    {
        Assert.Equal(0, Px(TrayIconArt.RenderRgba(TrayIconKey.Idle, 32), 32, 0, 0).A);
    }

    [Fact]
    public void BackfillDots()
    {
        var f = Enumerable.Range(0, 4).Select(n => TrayIconArt.RenderRgba(TrayIconKey.Backfill0 + n, 32)).ToArray();
        Assert.Equal(((byte)0x64, (byte)0xB5, (byte)0xF6, (byte)255), Px(f[0], 32, 3, 3));
        Assert.Equal(0, Px(f[1], 32, 3, 3).A);
        Assert.Equal(255, Px(f[1], 32, 28, 3).A);
        Assert.Equal(0, Px(f[0], 32, 28, 3).A);
        Assert.Equal(255, Px(f[2], 32, 28, 28).A);
        Assert.Equal(255, Px(f[3], 32, 3, 28).A);
        for (var i = 0; i < 4; i++)
        {
            for (var j = i + 1; j < 4; j++)
            {
                Assert.NotEqual(f[i], f[j]);
            }
        }
    }

    [Fact]
    public void UnavailableBadge()
    {
        var b = TrayIconArt.RenderRgba(TrayIconKey.Unavailable, 32);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Px(b, 32, 28, 22));
        Assert.Equal(((byte)0x42, (byte)0x42, (byte)0x42, (byte)255), Px(b, 32, 24, 22));
    }

    [Fact]
    public void Deterministic() =>
        Assert.Equal(TrayIconArt.BuildIco(TrayIconKey.Idle), TrayIconArt.BuildIco(TrayIconKey.Idle));

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
    private static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);

    [Fact]
    public void IcoStructure()
    {
        var a = new byte[2 * 2 * 4];
        a[0] = 1; a[1] = 2; a[2] = 3; a[3] = 4;           // top-left opaque-ish
        // pixels 1..3 stay alpha 0
        var b = new byte[3 * 3 * 4];
        var ico = IcoEncoder.Encode([(2, a), (3, b)]);

        Assert.Equal(new byte[] { 0, 0, 1, 0, 2, 0 }, ico[..6]);
        Assert.Equal(2, ico[6]);
        Assert.Equal(2, ico[7]);
        Assert.Equal(1, U16(ico, 10));
        Assert.Equal(32, U16(ico, 12));
        Assert.Equal(64, I32(ico, 14));
        Assert.Equal(38, I32(ico, 18));
        Assert.Equal(102, I32(ico, 34));
        Assert.Equal(88, I32(ico, 30));
        Assert.Equal(190, ico.Length);
        Assert.Equal(4, I32(ico, 38 + 8));
        Assert.Equal(6, I32(ico, 102 + 8));

        // Image 0 pixel data starts at 38+40; the last row (index 1) holds source row 0.
        var last = 38 + 40 + 2 * 4;
        Assert.Equal(new byte[] { 3, 2, 1, 4 }, ico[last..(last + 4)]);
        // Mask: bottom-up; last mask row = source row 0: pixel 0 opaque (bit clear), pixel 1 transparent (bit set).
        var maskStart = 38 + 40 + 16;
        Assert.Equal(0b0100_0000, ico[maskStart + 4]);
        Assert.Equal(0b1100_0000, ico[maskStart]);
    }

    [Fact]
    public void Ico256WritesZeroWidth()
    {
        var ico = IcoEncoder.Encode([(256, new byte[256 * 256 * 4])]);
        Assert.Equal(0, ico[6]);
        Assert.Equal(0, ico[7]);
    }

    [Fact]
    public void EncodeErrors()
    {
        Assert.Throws<ArgumentException>(() => IcoEncoder.Encode([]));
        Assert.Throws<ArgumentException>(() => IcoEncoder.Encode([(2, new byte[3])]));
        Assert.Throws<ArgumentException>(() => IcoEncoder.Encode([(0, Array.Empty<byte>())]));
        Assert.Throws<ArgumentException>(() => IcoEncoder.Encode([(1, new byte[4]), (1, new byte[4])]));
    }

    [Fact]
    public void BuildIcoSizes()
    {
        var ico = TrayIconArt.BuildIco(TrayIconKey.Busy);
        Assert.Equal(4, U16(ico, 4));
        Assert.Equal(new byte[] { 16, 20, 24, 32 }, new[] { ico[6], ico[22], ico[38], ico[54] });
    }

    [Fact]
    public void FileNames()
    {
        Assert.Equal("backfill2.ico", TrayIconArt.FileName(TrayIconKey.Backfill2));
        Assert.Equal(11, Enum.GetValues<TrayIconKey>().Select(TrayIconArt.FileName).Distinct().Count());
    }

    private static string? FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ClippysRevenge.slnx")))
        {
            d = d.Parent;
        }
        return d?.FullName;
    }

    [Fact]
    public void CheckedInIcons_MatchGenerator()
    {
        var root = FindRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root, "src", "ClippysRevenge.App", "Tray", "Icons");
        var regen = Environment.GetEnvironmentVariable("CLIPPY_REGEN_ICONS") == "1";
        if (regen)
        {
            Directory.CreateDirectory(dir);
        }
        foreach (var key in Enum.GetValues<TrayIconKey>())
        {
            var name = TrayIconArt.FileName(key);
            var path = Path.Combine(dir, name);
            var expected = TrayIconArt.BuildIco(key);
            if (regen)
            {
                File.WriteAllBytes(path, expected);
                continue;
            }
            Assert.True(File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected),
                $"{name} differs from TrayIconArt; run with CLIPPY_REGEN_ICONS=1 to regenerate.");
        }
    }
}

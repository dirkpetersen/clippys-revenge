namespace ClippysRevenge.Core;

/// <summary>
/// Original paperclip tray icons, rasterized with plain double arithmetic (no trigonometry) so the
/// output is identical on every platform. The checked-in .ico files must match <see cref="BuildIco"/>.
/// </summary>
public static class TrayIconArt
{
    private const double HalfWidth = 1.4;

    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32];

    public static string FileName(TrayIconKey key) => key.ToString().ToLowerInvariant() + ".ico";

    public static byte[] BuildIco(TrayIconKey key)
    {
        var images = new List<(int Size, byte[] Rgba)>();
        foreach (var s in Sizes)
        {
            images.Add((s, RenderRgba(key, s)));
        }
        return IcoEncoder.Encode(images);
    }

    public static byte[] RenderRgba(TrayIconKey key, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256);
        if (!Enum.IsDefined(key))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        var clip = ClipColor(key);
        var scale = size / 32.0;
        var unit = 32.0 / size;
        var result = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5) * unit;
                var v = (y + 0.5) * unit;
                double da = 0, dr = 0, dg = 0, db = 0;

                void Over((double R, double G, double B) c, double sa)
                {
                    if (sa <= 0)
                    {
                        return;
                    }
                    var oa = sa + da * (1 - sa);
                    var k = da * (1 - sa);
                    dr = oa == 0 ? 0 : (c.R * sa + dr * k) / oa;
                    dg = oa == 0 ? 0 : (c.G * sa + dg * k) / oa;
                    db = oa == 0 ? 0 : (c.B * sa + db * k) / oa;
                    da = oa;
                }

                Over(clip, Math.Clamp(HalfWidth * scale + 0.5 - ClipDistance(u, v) * scale, 0, 1));

                if (key == TrayIconKey.Unavailable)
                {
                    Over(Rgb(0xFF, 0xFF, 0xFF), Fill(Dist(u, v, 25, 25), 6.5, scale));
                    var mark = Rgb(0x42, 0x42, 0x42);
                    Over(mark, Fill(CapsuleDist(u, v, 25, 20.5, 25, 25.5), 1.2, scale));
                    Over(mark, Fill(Dist(u, v, 25, 28.3), 1.3, scale));
                }
                else if (key >= TrayIconKey.Backfill0)
                {
                    var n = key - TrayIconKey.Backfill0;
                    var cx = n is 0 or 3 ? 3.5 : 28.5;
                    var cy = n is 0 or 1 ? 3.5 : 28.5;
                    Over(Rgb(0x64, 0xB5, 0xF6), Fill(Dist(u, v, cx, cy), 2.5, scale));
                }

                var i = (y * size + x) * 4;
                result[i] = ToByte(dr);
                result[i + 1] = ToByte(dg);
                result[i + 2] = ToByte(db);
                result[i + 3] = ToByte(da);
            }
        }
        return result;
    }

    private static byte ToByte(double v) => (byte)(int)(v * 255 + 0.5);

    private static (double R, double G, double B) Rgb(int r, int g, int b) => (r / 255.0, g / 255.0, b / 255.0);

    private static (double R, double G, double B) ClipColor(TrayIconKey key) => key switch
    {
        TrayIconKey.Idle => Rgb(0x2E, 0x7D, 0x32),
        TrayIconKey.Serving => Rgb(0x66, 0xBB, 0x6A),
        TrayIconKey.Paused or TrayIconKey.Unavailable => Rgb(0x75, 0x75, 0x75),
        TrayIconKey.Confirm => Rgb(0xFF, 0xB3, 0x00),
        TrayIconKey.Error => Rgb(0xC6, 0x28, 0x28),
        _ => Rgb(0x15, 0x65, 0xC0),
    };

    private static double Fill(double dist, double radius, double scale) =>
        Math.Clamp(0.5 - (dist - radius) * scale, 0, 1);

    private static double Dist(double u, double v, double cx, double cy)
    {
        var dx = u - cx;
        var dy = v - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SegmentDist(double u, double v, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((u - ax) * dx + (v - ay) * dy) / len2, 0, 1);
        return Dist(u, v, ax + t * dx, ay + t * dy);
    }

    private static double CapsuleDist(double u, double v, double ax, double ay, double bx, double by) =>
        SegmentDist(u, v, ax, ay, bx, by);

    private static double SemiDist(double u, double v, double cx, double cy, double r, bool bottom)
    {
        var onSide = bottom ? v >= cy : v <= cy;
        if (onSide)
        {
            return Math.Abs(Dist(u, v, cx, cy) - r);
        }
        return Math.Min(Dist(u, v, cx - r, cy), Dist(u, v, cx + r, cy));
    }

    private static double ClipDistance(double u, double v)
    {
        var d = SegmentDist(u, v, 13, 10, 13, 21);
        d = Math.Min(d, SemiDist(u, v, 16, 21, 3, true));
        d = Math.Min(d, SegmentDist(u, v, 19, 21, 19, 7));
        d = Math.Min(d, SemiDist(u, v, 13.5, 7, 5.5, false));
        d = Math.Min(d, SegmentDist(u, v, 8, 7, 8, 22));
        d = Math.Min(d, SemiDist(u, v, 16, 22, 8, true));
        d = Math.Min(d, SegmentDist(u, v, 24, 22, 24, 10));
        return d;
    }
}

public static class IcoEncoder
{
    public static byte[] Encode(IReadOnlyList<(int Size, byte[] Rgba)> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(images));
        }
        var seen = new HashSet<int>();
        foreach (var (size, rgba) in images)
        {
            if (size is < 1 or > 256)
            {
                throw new ArgumentException("Image size must be 1..256.", nameof(images));
            }
            if (rgba is null || rgba.Length != size * size * 4)
            {
                throw new ArgumentException("Pixel buffer length must be size*size*4.", nameof(images));
            }
            if (!seen.Add(size))
            {
                throw new ArgumentException("Duplicate image size.", nameof(images));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, _) in images)
        {
            var maskBytes = ((size + 31) / 32) * 4 * size;
            var bytesInRes = 40 + size * size * 4 + maskBytes;
            w.Write((byte)(size == 256 ? 0 : size));
            w.Write((byte)(size == 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(bytesInRes);
            w.Write(offset);
            offset += bytesInRes;
        }

        foreach (var (size, rgba) in images)
        {
            var rowMask = ((size + 31) / 32) * 4;
            var maskBytes = rowMask * size;
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);
            w.Write(size * size * 4 + maskBytes);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);

            for (var y = size - 1; y >= 0; y--)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;
                    w.Write(rgba[i + 2]);
                    w.Write(rgba[i + 1]);
                    w.Write(rgba[i]);
                    w.Write(rgba[i + 3]);
                }
            }

            var mask = new byte[rowMask];
            for (var y = size - 1; y >= 0; y--)
            {
                Array.Clear(mask);
                for (var x = 0; x < size; x++)
                {
                    if (rgba[(y * size + x) * 4 + 3] == 0)
                    {
                        mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
                w.Write(mask);
            }
        }
        w.Flush();
        return ms.ToArray();
    }
}

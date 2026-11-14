using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>Badged tray icons (SPEC 22.6). UI thread only (not thread-safe).</summary>
internal sealed class TrayBadgeRenderer : IDisposable
{
    private readonly Dictionary<TrayIconKey, Icon> _current = [];
    private readonly List<Icon> _retired = [];
    private readonly Dictionary<string, byte[]> _layers = [];
    private string _text = "";
    private bool _disposed;

    /// <exception cref="ArgumentOutOfRangeException">iconPx is not 16, 20, 24 or 32.</exception>
    public TrayBadgeRenderer(int iconPx)
    {
        if (!TrayIconArt.Sizes.Contains(iconPx))
        {
            throw new ArgumentOutOfRangeException(nameof(iconPx), iconPx, "Icon size must be 16, 20, 24 or 32.");
        }
        IconPx = iconPx;
    }

    public int IconPx { get; }

    /// <summary>Icons currently held (current + retired generation). For tests.</summary>
    internal int HeldIcons => _current.Count + _retired.Count;

    /// <summary>
    /// The state icon <paramref name="key"/> with the badge <paramref name="text"/>, cached per (key, text).
    /// The returned Icon stays valid until Get is called with a second different text after it, or Dispose.
    /// </summary>
    /// <exception cref="ArgumentException">text is "" (no badge: use the plain icon).</exception>
    /// <exception cref="ObjectDisposedException">after Dispose.</exception>
    /// <exception cref="ExternalException">GDI+ failed (caller falls back to the plain icon).</exception>
    public Icon Get(TrayIconKey key, string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);

        if (text != _text)
        {
            // Two generations: the NotifyIcon still shows an icon of the previous text until the caller assigns
            // the new one, so only the generation before that is disposed.
            foreach (var old in _retired)
            {
                old.Dispose();
            }
            _retired.Clear();
            _retired.AddRange(_current.Values);
            _current.Clear();
            _text = text;
        }
        if (_current.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (!_layers.TryGetValue(text, out var layer))
        {
            layer = RenderLayer(IconPx, text);
            _layers[text] = layer;
        }
        var rgba = AttentionBadge.Composite(TrayIconArt.RenderRgba(key, IconPx), layer, IconPx,
            AttentionBadge.Layout(IconPx, text).Badge);
        var ico = IcoEncoder.Encode([(IconPx, rgba)]);
        using var ms = new MemoryStream(ico);
        var icon = new Icon(ms, IconPx, IconPx);
        try
        {
            _ = icon.Handle;   // a bad image fails here, not inside NotifyIcon
        }
        catch
        {
            icon.Dispose();
            throw;
        }
        _current[key] = icon;
        return icon;
    }

    /// <summary>The badge alone on a transparent px×px canvas, RGBA (GDI+). Alpha is 0 outside Layout.Badge.</summary>
    internal static byte[] RenderLayer(int iconPx, string text)
    {
        var l = AttentionBadge.Layout(iconPx, text);
        var result = new byte[iconPx * iconPx * 4];

        using (var bmp = new Bitmap(iconPx, iconPx, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;

                var ink = AttentionBadge.Ink;
                var fill = AttentionBadge.Fill;
                using var inkBrush = new SolidBrush(Color.FromArgb(ink.R, ink.G, ink.B));
                using var fillBrush = new SolidBrush(Color.FromArgb(fill.R, fill.G, fill.B));
                using (var outer = Capsule(l.Badge))
                {
                    g.FillPath(inkBrush, outer);
                }
                using (var inner = Capsule(l.Inner))
                {
                    g.FillPath(fillBrush, inner);
                }

                FontFamily family;
                try
                {
                    family = new FontFamily("Segoe UI");
                }
                catch (ArgumentException)
                {
                    family = FontFamily.GenericSansSerif;
                }
                using (family)
                {
                    var style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                    using var fmt = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip);
                    using var path = new GraphicsPath();
                    path.AddString(text, family, (int)style, l.FontPx, PointF.Empty, fmt);
                    var b = path.GetBounds();
                    if (b.Width > 0 && b.Height > 0)
                    {
                        var k = Math.Min(1f, Math.Min(l.TextBox.Width / b.Width, l.TextBox.Height / b.Height));
                        using var m = new Matrix();
                        m.Translate(-b.X - b.Width / 2f, -b.Y - b.Height / 2f);
                        m.Scale(k, k, MatrixOrder.Append);
                        m.Translate(l.TextBox.X + l.TextBox.Width / 2f, l.TextBox.Y + l.TextBox.Height / 2f, MatrixOrder.Append);
                        path.Transform(m);
                        g.FillPath(inkBrush, path);
                    }
                }
            }

            var data = bmp.LockBits(new Rectangle(0, 0, iconPx, iconPx), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[iconPx * 4];
                for (var y = 0; y < iconPx; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < iconPx; x++)
                    {
                        var s = x * 4;
                        var d = (y * iconPx + x) * 4;
                        result[d] = row[s + 2];
                        result[d + 1] = row[s + 1];
                        result[d + 2] = row[s];
                        result[d + 3] = row[s + 3];
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // Guard: anti-aliasing never reaches past the rect, but nothing outside it may be non-transparent.
        for (var y = 0; y < iconPx; y++)
        {
            for (var x = 0; x < iconPx; x++)
            {
                if (!l.Badge.Contains(x, y))
                {
                    Array.Clear(result, (y * iconPx + x) * 4, 4);
                }
            }
        }
        return result;
    }

    /// <summary>TrayIconArt.RenderRgba(key, px) with the layer composited inside Layout.Badge (AttentionBadge.Composite).</summary>
    internal static byte[] ComposeRgba(TrayIconKey key, int iconPx, string text) =>
        AttentionBadge.Composite(TrayIconArt.RenderRgba(key, iconPx), RenderLayer(iconPx, text), iconPx,
            AttentionBadge.Layout(iconPx, text).Badge);

    private static GraphicsPath Capsule(PixelRect r)
    {
        var path = new GraphicsPath();
        if (r.Width == r.Height)
        {
            path.AddEllipse(r.X, r.Y, r.Width, r.Height);
            return path;
        }
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var i in _current.Values)
        {
            i.Dispose();
        }
        foreach (var i in _retired)
        {
            i.Dispose();
        }
        _current.Clear();
        _retired.Clear();
        _layers.Clear();
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>Something that can need the user's attention (SPEC 22.6). Count is >= 0 and cheap (no I/O, no COM).</summary>
public interface IAttentionSource
{
    int Count { get; }

    /// <summary>Raised on any thread after Count may have changed.</summary>
    event Action? Changed;
}

/// <summary>Sum of all attention sources (SPEC 22.6). Thread-safe. Lives as long as the process.</summary>
public sealed class AttentionCount
{
    private readonly IAttentionSource[] _sources;
    private readonly ILogger? _log;
    private readonly Lock _lock = new();
    private int _total;

    /// <exception cref="ArgumentNullException">sources is null.</exception>
    /// <exception cref="ArgumentException">an element is null.</exception>
    /// <remarks>Subscribes to every source's Changed and computes Total once, here.</remarks>
    public AttentionCount(IEnumerable<IAttentionSource> sources, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        if (_sources.Any(s => s is null))
        {
            throw new ArgumentException("A source is null.", nameof(sources));
        }
        _log = logger;
        _total = TryRead() ?? 0;
        foreach (var s in _sources)
        {
            s.Changed += OnSourceChanged;
        }
    }

    /// <summary>Last computed sum. Negative source counts count as 0; saturates at int.MaxValue.</summary>
    public int Total
    {
        get { lock (_lock) return _total; }
    }

    /// <summary>Raised (any thread) only when Total changed. Each handler called separately; exceptions caught and
    /// logged (Warning, type only).</summary>
    public event Action? Changed;

    private int? TryRead()
    {
        try
        {
            long sum = 0;
            foreach (var s in _sources)
            {
                sum += Math.Max(0, s.Count);
            }
            return (int)Math.Min(sum, int.MaxValue);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Attention count could not be read ({Type}).", ex.GetType().Name);
            return null;
        }
    }

    private void OnSourceChanged()
    {
        bool changed;
        lock (_lock)
        {
            var read = TryRead();
            if (read is not { } now)
            {
                return;
            }
            changed = now != _total;
            _total = now;
        }
        if (!changed)
        {
            return;
        }
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        foreach (var h in handlers.GetInvocationList())
        {
            try
            {
                ((Action)h)();
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Attention count handler failed ({Type}).", ex.GetType().Name);
            }
        }
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;      // exclusive
    public int Bottom => Y + Height;    // exclusive
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public PixelRect Inset(int d) => new(X + d, Y + d, Width - 2 * d, Height - 2 * d);
}

/// <param name="Badge">The whole badge (outline included), flush with the icon's right and top edges.</param>
/// <param name="Inner">Badge inset by Outline: the yellow area.</param>
/// <param name="TextBox">Inner inset by 1: the digits are scaled and centered to fit here.</param>
/// <param name="FontPx">Em size in pixels before fitting.</param>
public sealed record BadgeLayout(int IconPx, PixelRect Badge, int Outline, PixelRect Inner, PixelRect TextBox, int FontPx);

/// <summary>Attention badge text, geometry, blending and tooltip wording (SPEC 22.6). Pure.</summary>
public static class AttentionBadge
{
    public const int MaxShown = 9;
    public static readonly (byte R, byte G, byte B) Fill = (0xFF, 0xD4, 0x00);
    public static readonly (byte R, byte G, byte B) Ink = (0x1A, 0x1A, 0x1A);

    /// <summary>"" for 0, "1".."9", "9+" above 9.</summary>
    /// <exception cref="ArgumentOutOfRangeException">count &lt; 0.</exception>
    public static string Text(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return "";
        }
        return count > MaxShown ? "9+" : count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The icon size to render for a system small-icon size: the smallest of 16/20/24/32 that is
    /// >= requestedPx, else 32.</summary>
    /// <exception cref="ArgumentOutOfRangeException">requestedPx &lt; 1.</exception>
    public static int PickIconSize(int requestedPx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPx, 1);
        return requestedPx <= 16 ? 16 : requestedPx <= 20 ? 20 : requestedPx <= 24 ? 24 : 32;
    }

    /// <summary>Badge geometry: flush top-right on every state, so it never covers the Outlook-unavailable "!" disc.
    /// Wide (for "9+") when text has 2 characters.</summary>
    /// <exception cref="ArgumentOutOfRangeException">iconPx is not 16, 20, 24 or 32.</exception>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    /// <exception cref="ArgumentException">text is "" or longer than 2 characters.</exception>
    public static BadgeLayout Layout(int iconPx, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > 2)
        {
            throw new ArgumentException("Badge text must be 1 or 2 characters.", nameof(text));
        }
        var wide = text.Length == 2;
        var (h, wNarrow, wWide, outline, font) = iconPx switch
        {
            16 => (10, 10, 15, 1, 9),
            20 => (12, 12, 18, 1, 11),
            24 => (14, 14, 21, 1, 13),
            32 => (19, 19, 28, 2, 17),
            _ => throw new ArgumentOutOfRangeException(nameof(iconPx), iconPx, "Icon size must be 16, 20, 24 or 32."),
        };
        var w = wide ? wWide : wNarrow;
        var badge = new PixelRect(iconPx - w, 0, w, h);
        var inner = badge.Inset(outline);
        return new BadgeLayout(iconPx, badge, outline, inner, inner.Inset(1), font);
    }

    /// <summary>
    /// Returns a new RGBA buffer: baseRgba with layerRgba blended over it (straight alpha, source-over) inside
    /// <paramref name="area"/> only. Pixels outside area, and inside pixels whose layer alpha is 0, are copied from
    /// baseRgba byte for byte. Inputs are not modified.
    /// </summary>
    /// <exception cref="ArgumentNullException">a buffer is null.</exception>
    /// <exception cref="ArgumentException">a buffer's length isn't iconPx*iconPx*4.</exception>
    /// <exception cref="ArgumentOutOfRangeException">iconPx &lt; 1, or area not inside [0, iconPx)².</exception>
    public static byte[] Composite(byte[] baseRgba, byte[] layerRgba, int iconPx, PixelRect area)
    {
        ArgumentNullException.ThrowIfNull(baseRgba);
        ArgumentNullException.ThrowIfNull(layerRgba);
        ArgumentOutOfRangeException.ThrowIfLessThan(iconPx, 1);
        var len = iconPx * iconPx * 4;
        if (baseRgba.Length != len || layerRgba.Length != len)
        {
            throw new ArgumentException("Pixel buffer length must be iconPx*iconPx*4.");
        }
        if (area.X < 0 || area.Y < 0 || area.Width < 0 || area.Height < 0 || area.Right > iconPx || area.Bottom > iconPx)
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "Area must lie inside the icon.");
        }

        var result = (byte[])baseRgba.Clone();
        for (var y = area.Y; y < area.Bottom; y++)
        {
            for (var x = area.X; x < area.Right; x++)
            {
                var i = (y * iconPx + x) * 4;
                if (layerRgba[i + 3] == 0)
                {
                    continue;
                }
                var sa = layerRgba[i + 3] / 255.0;
                var da = baseRgba[i + 3] / 255.0;
                var oa = sa + da * (1 - sa);
                for (var c = 0; c < 3; c++)
                {
                    var v = oa == 0 ? 0.0 : (layerRgba[i + c] / 255.0 * sa + baseRgba[i + c] / 255.0 * da * (1 - sa)) / oa;
                    result[i + c] = ToByte(v);
                }
                result[i + 3] = ToByte(oa);
            }
        }
        return result;
    }

    private static byte ToByte(double v) => (byte)(int)(Math.Clamp(v, 0, 1) * 255 + 0.5);

    /// <summary>"" for 0; " · 1 draft invite needs your attention"; " · &lt;n&gt; draft invites need your attention"
    /// with n in invariant N0 ("1,240").</summary>
    /// <exception cref="ArgumentOutOfRangeException">count &lt; 0.</exception>
    public static string TooltipSuffix(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return "";
        }
        return " · " + count.ToString("N0", CultureInfo.InvariantCulture)
            + (count == 1 ? " draft invite needs your attention" : " draft invites need your attention");
    }
}

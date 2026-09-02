using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LlamaTray;

internal enum ServerState
{
    Stopped,
    StartedNoModel,
    ModelLoaded,
}

/// <summary>
/// Builds the tray icon by recoloring the orange element of the llama-cpp logo to a
/// state color (hue-shift that preserves the logo's lightness/saturation shading and
/// its alpha). Stopped = red, idle (server up, no model) = blue, loaded = green, and
/// any in-progress operation (loading/unloading) keeps the logo orange with an
/// orbiting activity dot.
/// </summary>
internal static class IconFactory
{
    public const int TotalAnimationFrames = 8;

    // The orange in the source llama-cpp logo (RGB 255,130,54).
    private static readonly Color SourceOrange = Color.FromArgb(255, 130, 54);

    // State overlay colors (recolor the orange element to these).
    private static readonly Color StoppedColor = Color.FromArgb(220, 53, 69);   // red
    private static readonly Color IdleColor = Color.FromArgb(0, 123, 255);      // blue
    private static readonly Color LoadedColor = Color.FromArgb(40, 167, 69);   // green
    private static readonly Color LoadingColor = SourceOrange;                  // orange (unchanged)

    private static readonly Image? BaseLlama;
    private static readonly Dictionary<ServerState, Icon> Cache = new();
    private static readonly object FrameCacheLock = new();
    private static readonly Dictionary<(ServerState State, int Frame), Icon?> _frameCache = new();
    private static readonly Dictionary<Color, Bitmap> _recoloredCache = new();

    static IconFactory()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "llama-icon.png");
        BaseLlama = File.Exists(iconPath) ? Image.FromFile(iconPath) : null;
    }

    public static Icon Get(ServerState state) =>
        Cache.TryGetValue(state, out var cached) ? cached : Cache[state] = Render(StateColor(state), null);

    /// <summary>Returns the plain llama-cpp logo (orange) without a state overlay.</summary>
    public static Icon GetBaseIcon() => Render(SourceOrange, null);

    /// <summary>
    /// Returns an animated frame for the given state, or null when no animation is
    /// available (Stopped has no activity to indicate). An animating icon means an
    /// operation is in progress, so it keeps the logo orange (loading color).
    /// </summary>
    public static Icon? GetAnimatedFrame(ServerState state, int frameIndex, int totalFrames)
    {
        if (state == ServerState.Stopped)
            return null;

        var cacheKey = (state, frameIndex);
        lock (FrameCacheLock)
        {
            if (_frameCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var icon = Render(LoadingColor, frameIndex);
        lock (FrameCacheLock) _frameCache[cacheKey] = icon;
        return icon;
    }

    private static Color StateColor(ServerState state) =>
        state switch
        {
            ServerState.Stopped => StoppedColor,
            ServerState.StartedNoModel => IdleColor,
            ServerState.ModelLoaded => LoadedColor,
            _ => Color.Gray,
        };

    private static Icon Render(Color color, int? frameIndex)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (BaseLlama != null)
            {
                using var rec = GetRecolored(color);
                g.DrawImage(rec, 0, 0, size, size);
            }
            else
            {
                // Fallback: plain colored circle.
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, new Rectangle(2, 2, size - 4, size - 4));
            }

            // Orbiting activity dot while an operation is in progress.
            if (frameIndex.HasValue)
            {
                var cx = size / 2f;
                var cy = size / 2f;
                var radius = size / 2f - 4;
                var angle = (2 * Math.PI * frameIndex.Value) / TotalAnimationFrames - Math.PI / 2;
                var dotX = cx + radius * (float)Math.Cos(angle);
                var dotY = cy + radius * (float)Math.Sin(angle);
                using var dotBrush = new SolidBrush(Color.White);
                g.FillEllipse(dotBrush, dotX - 2, dotY - 2, 4, 4);
            }
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Returns a cached copy of the logo with its orange element hue-shifted to
    /// <paramref name="color"/>. The logo's lightness, saturation and alpha are kept so
    /// its shading and anti-aliased edges are preserved in the new color.
    /// </summary>
    private static Bitmap GetRecolored(Color color)
    {
        lock (_recoloredCache)
        {
            if (_recoloredCache.TryGetValue(color, out var cached)) return cached;
        }

        var bmp = Recolor(BaseLlama!, color);
        lock (_recoloredCache) _recoloredCache[color] = bmp;
        return bmp;
    }

    private static Bitmap Recolor(Image source, Color target)
    {
        var w = source.Width;
        var h = source.Height;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(source, 0, 0, w, h);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var bytes = Math.Abs(data.Stride) * h;
        var vals = new byte[bytes];
        Marshal.Copy(data.Scan0, vals, 0, bytes);

        var targetH = RgbToHsl(target.R, target.G, target.B).H;
        for (var i = 0; i < vals.Length; i += 4)
        {
            var a = vals[i + 3];
            if (a == 0)
                continue;

            var r = vals[i + 2];
            var g = vals[i + 1];
            var b = vals[i];
            var hsl = RgbToHsl(r, g, b);
            // Skip near-white/gray pixels (none expected in the logo, but keeps
            // any stray highlights untouched).
            if (hsl.S < 0.05f)
                continue;

            var rgb = HslToRgb(targetH, hsl.S, hsl.L);
            vals[i] = rgb.B;
            vals[i + 1] = rgb.G;
            vals[i + 2] = rgb.R;
        }

        Marshal.Copy(vals, 0, data.Scan0, bytes);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static (float H, float S, float L) RgbToHsl(int r, int g, int b)
    {
        var rf = r / 255f;
        var gf = g / 255f;
        var bf = b / 255f;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2f;
        var h = 0f;
        var s = 0f;
        var d = max - min;
        if (d > 1e-6f)
        {
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == rf)
                h = (gf - bf) / d + (gf < bf ? 6f : 0f);
            else if (max == gf)
                h = (bf - rf) / d + 2f;
            else
                h = (rf - gf) / d + 4f;
            h *= 60f;
        }

        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
    {
        if (s <= 1e-6f)
        {
            var v = (byte)Math.Round(l * 255f);
            return (v, v, v);
        }

        var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        var p = 2f * l - q;
        var hn = h / 360f;
        var r = HueToRgb(p, q, hn + 1f / 3f);
        var g = HueToRgb(p, q, hn);
        var b = HueToRgb(p, q, hn - 1f / 3f);
        return ((byte)Math.Round(r * 255f), (byte)Math.Round(g * 255f), (byte)Math.Round(b * 255f));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

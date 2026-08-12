using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace LlamaTray;

internal enum ServerState
{
    Stopped,
    StartedNoModel,
    ModelLoaded,
}

internal static class IconFactory
{
    private static readonly Dictionary<ServerState, Icon> Cache = new();
    private static readonly object FrameCacheLock = new();
    private static readonly Dictionary<(ServerState State, int Frame, int Total), Icon?> _frameCache = new();
    public const int TotalAnimationFrames = 8;

    private static readonly Image BaseLlama;

    static IconFactory()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "llama-icon.png");
        BaseLlama = File.Exists(iconPath)
            ? Image.FromFile(iconPath)
            : null;
    }

    public static Icon Get(ServerState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        var color = state switch
        {
            ServerState.Stopped => Color.FromArgb(220, 53, 69),   // red
            ServerState.StartedNoModel => Color.FromArgb(255, 176, 32), // amber
            ServerState.ModelLoaded => Color.FromArgb(40, 167, 69),  // green
            _ => Color.Gray,
        };

        var icon = Render(color);
        Cache[state] = icon;
        return icon;
    }

    /// <summary>
    /// Returns an animated frame for the given state, or null when no animation is
    /// available for that state (e.g. Stopped has no "spinning" variant).
    /// </summary>
    public static Icon? GetAnimatedFrame(ServerState state, int frameIndex, int totalFrames)
    {
        var cacheKey = (state, frameIndex, totalFrames);
        lock (FrameCacheLock)
        {
            if (_frameCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        // Stopped has no animation — no activity to indicate.
        if (state == ServerState.Stopped)
        {
            lock (FrameCacheLock) _frameCache[cacheKey] = null;
            return null;
        }

        var color = state switch
        {
            ServerState.StartedNoModel => Color.FromArgb(255, 176, 32),
            ServerState.ModelLoaded => Color.FromArgb(40, 167, 69),
            _ => Color.Gray,
        };

        var icon = RenderAnimated(color, frameIndex, totalFrames);
        lock (FrameCacheLock) _frameCache[cacheKey] = icon;
        return icon;
    }

    private static Icon Render(Color color)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (BaseLlama != null)
            {
                // Draw llama face scaled to fit
                g.DrawImage(BaseLlama, 0, 0, size, size);
            }
            else
            {
                // Fallback: plain colored circle
                using var brush = new SolidBrush(color);
                var rect = new Rectangle(2, 2, size - 4, size - 4);
                g.FillEllipse(brush, rect);
            }

            // Colored ring overlay to indicate state
            using var ringPen = new Pen(color, 3f);
            var ringRect = new Rectangle(1, 1, size - 2, size - 2);
            g.DrawEllipse(ringPen, ringRect);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>
    /// Renders the llama face with a colored ring and an orbiting dot.
    /// <paramref name="frameIndex"/> controls the dot position (0..totalFrames-1).
    /// </summary>
    private static Icon RenderAnimated(Color color, int frameIndex, int totalFrames)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Llama face or fallback circle
            if (BaseLlama != null)
            {
                g.DrawImage(BaseLlama, 0, 0, size, size);
            }
            else
            {
                using var brush = new SolidBrush(color);
                var rect = new Rectangle(2, 2, size - 4, size - 4);
                g.FillEllipse(brush, rect);
            }

            // Colored ring overlay
            using var ringPen = new Pen(color, 3f);
            var ringRect = new Rectangle(1, 1, size - 2, size - 2);
            g.DrawEllipse(ringPen, ringRect);

            // Orbiting dot.
            var cx = size / 2f;
            var cy = size / 2f;
            var radius = size / 2f - 4;
            var angle = (2 * Math.PI * frameIndex) / totalFrames - Math.PI / 2;
            var dotX = cx + radius * (float)Math.Cos(angle);
            var dotY = cy + radius * (float)Math.Sin(angle);

            using var dotBrush = new SolidBrush(Color.White);
            g.FillEllipse(dotBrush, dotX - 2, dotY - 2, 4, 4);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}

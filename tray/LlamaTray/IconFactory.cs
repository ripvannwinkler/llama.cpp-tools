using System.Drawing;
using System.Drawing.Drawing2D;

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
        const int size = 32; // rendered large, Windows scales down for the tray
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(Color.FromArgb(60, 0, 0, 0), 1.5f);
            var rect = new Rectangle(2, 2, size - 4, size - 4);
            g.FillEllipse(brush, rect);
            g.DrawEllipse(pen, rect);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>
    /// Renders a state-colored circle with a small white dot that orbits the perimeter.
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

            // Background circle (same as static version).
            using var brush = new SolidBrush(color);
            using var pen = new Pen(Color.FromArgb(60, 0, 0, 0), 1.5f);
            var rect = new Rectangle(2, 2, size - 4, size - 4);
            g.FillEllipse(brush, rect);
            g.DrawEllipse(pen, rect);

            // Orbiting dot.
            var cx = size / 2f;         // centre X
            var cy = size / 2f;         // centre Y
            var radius = size / 2f - 4; // distance from centre to dot centre
            var angle = (2 * Math.PI * frameIndex) / totalFrames - Math.PI / 2; // start from top
            var dotX = cx + radius * (float)Math.Cos(angle);
            var dotY = cy + radius * (float)Math.Sin(angle);

            using var dotBrush = new SolidBrush(Color.White);
            g.FillEllipse(dotBrush, dotX - 2, dotY - 2, 4, 4);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}

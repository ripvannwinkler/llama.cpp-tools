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
}

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace LlamaTray;

/// <summary>
/// Model status payload for the hover tooltip.
/// </summary>
internal sealed record ModelStatusInfo(
    string ModelId,
    string? CtxSize,
    string? BatchSize,
    string? UbatchSize,
    string? CacheTypeK,
    string? CacheTypeV,
    string? NgpuLayers,
    string? VramGiB
);

/// <summary>
/// Borderless popup form that appears near the cursor when the user clicks the header
/// item in the tray context menu. Shows the current model's runtime params
/// (ctx size, batch/ubatch, KV quant, GPU layers). Auto-hidden by the caller.
/// </summary>
internal sealed class TooltipForm : Form
{
    // Win32: set window region for rounded corners
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int x,
        int y,
        int nWidth,
        int nHeight,
        int nWidthEllipse,
        int nHeightEllipse
    );

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly Label _contentLabel;

    public TooltipForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 30);
        Opacity = 0.95;
        Padding = new Padding(0);
        Margin = new Padding(0);
        AutoScaleMode = AutoScaleMode.Dpi;

        _contentLabel = new Label
        {
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            UseMnemonic = false,
            AutoSize = false,
            Dock = DockStyle.Fill,
        };

        // Load a monospace font for aligned columns
        try
        {
            _contentLabel.Font = new Font("Consolas", 9f, FontStyle.Regular);
        }
        catch
        {
            _contentLabel.Font = SystemFonts.DialogFont;
        }

        Controls.Add(_contentLabel);
    }

    /// <summary>
    /// Populate and display the tooltip with model status data.
    /// </summary>
    public void ShowWith(ModelStatusInfo info, Point mousePosition)
    {
        var lines = new List<string>();

        // Model name (bold-ish — use a header line)
        lines.Add(info.ModelId);
        lines.Add(new string('━', Math.Min(info.ModelId.Length, 40)));

        // Params in key-value rows
        if (info.CtxSize != null)
            lines.Add($"ctx-size    {info.CtxSize}");
        if (info.BatchSize != null)
            lines.Add($"batch-size  {info.BatchSize}");
        if (info.UbatchSize != null)
            lines.Add($"ubatch-size {info.UbatchSize}");
        if (info.CacheTypeK != null || info.CacheTypeV != null)
        {
            var k = info.CacheTypeK ?? "—";
            var v = info.CacheTypeV ?? "—";
            lines.Add($"kv-quant    k={k}  v={v}");
        }
        if (info.NgpuLayers != null)
            lines.Add($"gpu-layers  {info.NgpuLayers}");
        if (info.VramGiB != null)
            lines.Add($"vram        {info.VramGiB} GiB");

        _contentLabel.Text = string.Join("\n", lines);

        // Measure needed size
        var sf = new StringFormat { FormatFlags = StringFormatFlags.NoClip };
        using var g = CreateGraphics()!;
        var sizeF = g.MeasureString(_contentLabel.Text!, _contentLabel.Font!, int.MaxValue, sf);
        var padding = 16;
        Width = (int)sizeF.Width + padding;
        Height = (int)sizeF.Height + padding;

        // Clamp min size
        if (Width < 200)
            Width = 200;
        if (Height < 40)
            Height = 40;

        // Position the popup at the anchor point (already offset from the menu).
        var x = mousePosition.X;
        var y = mousePosition.Y;

        // Keep on-screen (primary monitor)
        var screen = Screen.PrimaryScreen!.Bounds;
        if (x < 0)
            x = 4;
        if (y < 0)
            y = screen.Bottom - Height - 4;
        if (x + Width > screen.Right)
            x = screen.Right - Width - 4;

        Location = new Point(x, y);

        // Rounded corners via region
        var region = CreateRoundRectRgn(0, 0, Width, Height, 12, 12);
        SetWindowRgn(Handle, region, true);
        DeleteObject(region);

        if (!Visible)
            Show();
        else
            Refresh();
    }
}

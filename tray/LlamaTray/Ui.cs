using System.Runtime.InteropServices;

namespace LlamaTray;

/// <summary>
/// Shared styling for LlamaTray forms: fonts, dark colors, DPI-scaled spacing and the
/// dark title bar. All fixed pixel values are 96-DPI design values; scale them with
/// <see cref="Scale"/> before use.
/// </summary>
internal static class Ui
{
    // Uniform frame between window edge and content.
    public const int OuterMargin = 16;
    // Gap between related controls.
    public const int Spacing = 8;
    public const int ButtonWidth = 100;
    public const int ButtonHeight = 32;

    public static Font DefaultFont => new("Segoe UI", 9F);
    public static Font MonoFont => new("Consolas", 9F);
    public static Font MonoLogFont => new("Consolas", 10F);

    public static readonly Color WindowDark = Color.FromArgb(32, 32, 32);
    public static readonly Color PanelDark = Color.FromArgb(24, 24, 24);
    public static readonly Color ButtonFace = Color.FromArgb(51, 51, 55);
    public static readonly Color ButtonBorder = Color.FromArgb(85, 85, 90);

    /// <summary>
    /// Dark flat button — WinForms buttons inherit the parent BackColor otherwise,
    /// which renders black-on-dark against the dark panels.
    /// </summary>
    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = ButtonBorder;
        button.BackColor = ButtonFace;
        button.ForeColor = Color.White;
        button.MinimumSize = new Size(Scale(button, ButtonWidth), Scale(button, ButtonHeight));
    }

    public static int Scale(Control control, int value) =>
        (int)Math.Round(value * control.DeviceDpi / 96.0);

    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Opt the window into a dark title bar (call after the handle exists).</summary>
    public static void EnableDarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        }
        catch
        {
            // Older Windows without the attribute — just keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

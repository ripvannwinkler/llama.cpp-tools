using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LlamaTray;

/// <summary>
/// Log tail viewer plus a status pane: the top area shows the running model and its
/// key runtime parameters with Unload / Load… actions on the right, and the log
/// output fills the remaining space, anchored to the bottom of the window.
/// </summary>
internal sealed class LogViewerForm : Form
{
    private const int TailBytes = 512 * 1024;

    private readonly ServerController _controller;
    private string _logFile;
    private readonly RichTextBox _logText;
    private readonly Label _statusLabel;
    private readonly Panel _statusPanel;
    private readonly Button _unloadButton;
    private readonly Button _loadButton;
    private readonly System.Windows.Forms.Timer _logTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private long _lastFilePosition = -1; // -1 = not yet opened

    private Dictionary<string, Dictionary<string, string>> _presets = new();
    private string? _presetsModelId; // cached presets are valid for this model id only
    private string? _lastLoadedId;
    private bool _actionBusy;
    private readonly double _uiScale;

    // Window bounds persist in llamatray-ui.properties next to the exe.
    private const string BoundsFile = "llamatray-ui.properties";

    public LogViewerForm(ServerController controller)
    {
        _controller = controller;
        _logFile = controller.ActiveLogFile;
        _uiScale = DeviceDpi / 96.0;

        Text = $"LlamaTray — {Path.GetFileName(_logFile)}";
        Icon = IconFactory.GetBaseIcon();
        StartPosition = FormStartPosition.CenterScreen;
        // Scale everything explicitly (device pixels); AutoScaleMode would double-scale
        // the bounds restored from llamatray-ui.properties.
        var s = DeviceDpi / 96.0;
        Font = Ui.DefaultFont;
        BackColor = Ui.WindowDark;
        ClientSize = new Size((int)(1280 * s), (int)(720 * s)); // 16:9
        MinimumSize = new Size((int)(1024 * s), (int)(576 * s)); // 16:9
        Padding = new Padding(Ui.Scale(this, Ui.OuterMargin)); // uniform frame on all sides
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;

        if (TryLoadBounds(out var savedSize, out var savedLocation))
        {
            var screen = Screen.FromPoint(savedLocation).Bounds;
            var size = new Size(
                Math.Clamp(savedSize.Width, MinimumSize.Width, screen.Width),
                Math.Clamp(savedSize.Height, MinimumSize.Height, screen.Height)
            );
            var location = new Point(
                Math.Clamp(savedLocation.X, screen.Left, screen.Right - size.Width),
                Math.Clamp(savedLocation.Y, screen.Top, screen.Bottom - size.Height)
            );
            StartPosition = FormStartPosition.Manual;
            Size = size;
            Location = location;
        }

        _logText = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            Font = Ui.MonoLogFont,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Multiline = true,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            HideSelection = false,
        };

        // Fills the area below the status pane; the form padding provides the frame.
        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        logPanel.Controls.Add(_logText);

        var buttonGap = Ui.Scale(this, Ui.Spacing);
        _unloadButton = new Button
        {
            Text = "Unload",
            AutoSize = true, // height follows the DPI-scaled font
            Dock = DockStyle.Fill, // equal width, stretched to the panel
            Margin = new Padding(0, 0, 0, buttonGap),
            Enabled = false,
        };
        Ui.StyleButton(_unloadButton);
        _unloadButton.Click += async (_, _) => await UnloadCurrentAsync();

        _loadButton = new Button
        {
            Text = "Load…",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        Ui.StyleButton(_loadButton);
        _loadButton.Click += async (_, _) => await PickAndLoadAsync();

        // One column, two rows: both buttons share the widest button's width.
        // Row heights are absolute — Dock=Right would otherwise stretch the table to
        // the status pane's height and dump the leftover into the last row.
        var buttonPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Ui.PanelDark,
            ColumnCount = 1,
            RowCount = 2,
        };
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, _unloadButton.PreferredSize.Height + buttonGap));
        buttonPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, _loadButton.PreferredSize.Height));
        buttonPanel.Controls.Add(_unloadButton, 0, 0);
        buttonPanel.Controls.Add(_loadButton, 0, 1);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = Ui.MonoFont,
            BackColor = Ui.PanelDark,
            ForeColor = Color.Gainsboro,
            UseMnemonic = false,
        };

        _statusPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)(150 * s),
            BackColor = Ui.PanelDark,
            Padding = new Padding(
                Ui.Scale(this, Ui.OuterMargin),
                Ui.Scale(this, 12),
                Ui.Scale(this, Ui.OuterMargin),
                Ui.Scale(this, 12)),
        };
        _statusPanel.Controls.Add(_statusLabel);
        _statusPanel.Controls.Add(buttonPanel);

        Controls.Add(logPanel); // added first → docked last → fills the bottom area
        Controls.Add(_statusPanel);

        _logTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();
        RefreshLog();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    /// <summary>
    /// Loads the saved window bounds from llamatray-ui.properties next to the exe.
    /// Returns false if the file is missing or malformed.
    /// </summary>
    private static bool TryLoadBounds(out Size size, out Point location)
    {
        size = default;
        location = default;
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BoundsFile);
            if (!File.Exists(path))
                return false;

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                var sep = line.IndexOf('=');
                if (sep <= 0)
                    continue;
                props[line[..sep].Trim()] = line[(sep + 1)..].Trim();
            }

            size = new Size(
                int.Parse(props["logviewer.width"], CultureInfo.InvariantCulture),
                int.Parse(props["logviewer.height"], CultureInfo.InvariantCulture)
            );
            location = new Point(
                int.Parse(props["logviewer.x"], CultureInfo.InvariantCulture),
                int.Parse(props["logviewer.y"], CultureInfo.InvariantCulture)
            );
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Best-effort persist of the window bounds next to the exe.</summary>
    private void SaveBounds()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BoundsFile);
            File.WriteAllLines(
                path,
                [$"logviewer.x={Location.X.ToString(CultureInfo.InvariantCulture)}",
                 $"logviewer.y={Location.Y.ToString(CultureInfo.InvariantCulture)}",
                 $"logviewer.width={Size.Width.ToString(CultureInfo.InvariantCulture)}",
                 $"logviewer.height={Size.Height.ToString(CultureInfo.InvariantCulture)}"]
            );
        }
        catch
        {
            // Persistence is best-effort; ignore write failures.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.EnableDarkTitleBar(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (WindowState == FormWindowState.Normal)
            SaveBounds();
        _logTimer.Stop();
        _logTimer.Dispose();
        _statusTimer.Stop();
        _statusTimer.Dispose();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------
    // Status pane
    // ------------------------------------------------------------------

    private async Task RefreshStatusAsync()
    {
        if (_actionBusy)
            return; // buttons already disabled; refresh after the action completes

        var listening = _controller.IsPortListening();
        List<ModelInfo>? models = null;
        double? vramGiB = null;
        if (listening)
        {
            models = await _controller.GetModelsAsync();
            vramGiB = await Task.Run(_controller.GetVramUsageGiB);
        }

        var loadedId = models?.FirstOrDefault(m => m.Status?.Value == "loaded")?.Id;
        RenderStatus(listening, loadedId, models, vramGiB);
    }

    private void RenderStatus(bool listening, string? loadedId, List<ModelInfo>? models, double? vramGiB)
    {
        var lines = new List<string>();

        if (!listening)
        {
            lines.Add($"server    stopped (port {ServerConfig.Current.Port})");
            lines.Add("model     —");
        }
        else
        {
            lines.Add($"server    running · port {ServerConfig.Current.Port}");

            if (loadedId == null)
            {
                lines.Add("model     (none loaded)");
            }
            else
            {
                if (loadedId != _presetsModelId)
                {
                    _presets = IniParser.Parse(ServerConfig.Current.PresetIni);
                    _presetsModelId = loadedId;
                }

                string? Resolve(string key) => IniParser.Resolve(_presets, loadedId, key);

                var ctx = Resolve("ctx-size");
                var ctxDisplay =
                    ctx != null && long.TryParse(ctx, out var ctxVal)
                        ? $"{FormatCount(ctxVal)} ({ctxVal})"
                        : ctx ?? "—";

                lines.Add($"model     {loadedId}");
                lines.Add($"ctx       {ctxDisplay}");
                lines.Add($"kv-quant  k={Resolve("cache-type-k") ?? "f16"}  v={Resolve("cache-type-v") ?? "f16"}");
                lines.Add($"caps      {FormatCaps(Resolve)}");
                lines.Add(
                    $"gpu       layers={Resolve("n-gpu-layers") ?? "—"}"
                    + $"  flash-attn={Resolve("flash-attn") ?? "—"}"
                    + $"  spec={Resolve("spec-type") ?? "off"}"
                );
                lines.Add(
                    vramGiB.HasValue ? $"vram      {vramGiB.Value:0.0} GiB" : "vram      —"
                );
            }

            if (models == null)
                lines.Add("models    (server not responding)");
        }

        _statusLabel.Text = string.Join(Environment.NewLine, lines);

        // Size the status pane to its content so the log gets the rest.
        var lineCount = lines.Count;
        _statusPanel.Height = Math.Max(
            (int)(100 * _uiScale),
            lineCount * _statusLabel.Font.Height + _statusPanel.Padding.Vertical + (int)(8 * _uiScale)
        );

        _lastLoadedId = loadedId;
        SetButtonsEnabled();
    }

    /// <summary>
    /// Derives capability flags from the model's preset: reasoning (and its output
    /// format), vision (mmproj projector present), MTP / speculative decoding.
    /// </summary>
    private static string FormatCaps(Func<string, string?> resolve)
    {
        var caps = new List<string>();

        var reasoningOn = ResolveIsOn(resolve("reasoning")) || resolve("reasoning-format") != null;
        if (reasoningOn)
        {
            var format = resolve("reasoning-format");
            caps.Add(format != null ? $"reasoning({format})" : "reasoning");
        }

        if (!string.IsNullOrEmpty(resolve("mmproj")))
            caps.Add("vision");

        var spec = resolve("spec-type");
        if (spec == "draft-dflash")
            caps.Add("mtp(dflash)");
        else if (spec != null)
            caps.Add($"spec({spec})");

        return caps.Count > 0 ? string.Join(" · ", caps) : "—";
    }

    private static bool ResolveIsOn(string? value) =>
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Format a large number with K/M suffix using binary units (e.g. 262144 → "256K").</summary>
    private static string FormatCount(long value)
    {
        if (value >= 1_048_576)
            return (value / 1048576) + "M";
        if (value >= 1024)
            return (value / 1024) + "K";
        return value.ToString();
    }

    private void SetButtonsEnabled()
    {
        _unloadButton.Enabled = !_actionBusy && _lastLoadedId != null;
        _loadButton.Enabled = !_actionBusy;
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private async Task UnloadCurrentAsync()
    {
        if (_actionBusy || _lastLoadedId == null)
            return;

        _actionBusy = true;
        SetButtonsEnabled();
        try
        {
            var ok = await _controller.UnloadModelAsync(_lastLoadedId);
            if (!ok)
                MessageBox.Show(
                    this,
                    "Unload request failed — check the log.",
                    "Unload",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unload", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _actionBusy = false;
            await RefreshStatusAsync();
        }
    }

    private async Task PickAndLoadAsync()
    {
        if (_actionBusy)
            return;

        var ids = ModelCatalog.ReadModelIds();
        if (ids.Count == 0)
        {
            MessageBox.Show(
                this,
                "No models found in models.ini.",
                "Load model",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            return;
        }

        string? picked;
        using (var picker = new ModelPickerForm(ids, _lastLoadedId))
        {
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            picked = picker.SelectedModel;
        }

        if (picked == null || picked == _lastLoadedId)
            return;

        _actionBusy = true;
        SetButtonsEnabled();
        try
        {
            var (ok, message) = await _controller.LoadModelAsync(picked);
            if (!ok)
                MessageBox.Show(
                    this,
                    message,
                    "Load model",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load model", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _actionBusy = false;
            await RefreshStatusAsync();
        }
    }

    // ------------------------------------------------------------------
    // Log tail
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size;
        public uint Mask;
        public int Min;
        public int Max;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hWnd, int bar, ref ScrollInfo info);

    private const int WM_SETREDRAW = 0x000B;
    private const int EM_GETSCROLLPOS = 0x04DD;
    private const int EM_SETSCROLLPOS = 0x04DE;
    private const int SB_VERT = 1;
    private const uint SIF_ALL = 0x17;

    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-9;]*[a-zA-Z]",
        RegexOptions.Compiled);

    private static string StripAnsiEscapes(string text) =>
        string.IsNullOrEmpty(text) ? text : AnsiEscapeRegex.Replace(text, string.Empty);

    private void RefreshLog()
    {
        // Follow the controller's live log file: it changes on every server start
        // (unique temp name per launch).
        var activeLog = _controller.ActiveLogFile;
        if (!string.Equals(_logFile, activeLog, StringComparison.Ordinal))
        {
            _logFile = activeLog;
            _lastFilePosition = -1;
            _logText.Clear();
            Text = $"LlamaTray — {Path.GetFileName(_logFile)}";
        }

        try
        {
            if (!File.Exists(_logFile))
            {
                if (_logText.TextLength == 0)
                    AppendLogText("(waiting for log file...)");
                return;
            }

            using var stream = new FileStream(
                _logFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            var fileLength = stream.Length;

            // File was truncated or rotated — reset.
            if (_lastFilePosition > fileLength)
            {
                _lastFilePosition = -1;
                _logText.Clear();
            }

            if (_lastFilePosition == fileLength)
                return; // no new content

            // First open: seek to tail to avoid dumping the entire file.
            if (_lastFilePosition < 0)
                _lastFilePosition = Math.Max(0, fileLength - TailBytes);

            stream.Seek(_lastFilePosition, SeekOrigin.Begin);

            var length = checked((int)(fileLength - _lastFilePosition));
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            text = StripAnsiEscapes(text);
            text = NormalizeLineEndings(text);

            // On first open, trim to the last complete line.
            if (_lastFilePosition > 0 && _lastFilePosition == Math.Max(0, fileLength - TailBytes))
            {
                var firstNewline = text.IndexOf('\n');
                text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
            }

            // Anchor the next read to the bytes actually consumed.
            _lastFilePosition = stream.Position;

            if (string.IsNullOrEmpty(text))
                return;

            var followTail = IsScrolledToBottom();
            var scrollPosition = GetScrollPosition();
            AppendLogText(text);
            if (followTail)
            {
                _logText.SelectionStart = _logText.TextLength;
                _logText.ScrollToCaret();
                // Follow new lines vertically without resetting horizontal scrolling.
                SetScrollPosition(new NativePoint { X = scrollPosition.X, Y = GetScrollPosition().Y });
            }
            else
            {
                SetScrollPosition(scrollPosition);
            }
        }
        catch (IOException)
        {
            // The server may be rotating or replacing the file; retry on the next tick.
        }
        catch (UnauthorizedAccessException)
        {
            if (_logText.TextLength == 0)
                AppendLogText($"Unable to read log file:\r\n{_logFile}");
        }
    }

    private bool IsScrolledToBottom()
    {
        if (_logText.TextLength == 0) return true;

        var info = new ScrollInfo
        {
            Size = (uint)Marshal.SizeOf<ScrollInfo>(),
            Mask = SIF_ALL,
        };
        if (!GetScrollInfo(_logText.Handle, SB_VERT, ref info))
            return true;

        return info.Position + (int)info.Page >= info.Max;
    }

    private NativePoint GetScrollPosition()
    {
        var position = new NativePoint();
        SendMessage(_logText.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref position);
        return position;
    }

    private void SetScrollPosition(NativePoint position) =>
        SendMessage(_logText.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref position);

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private void AppendLogText(string text)
    {
        SendMessage(_logText.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            _logText.AppendText(text);
        }
        finally
        {
            SendMessage(_logText.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _logText.Invalidate();
        }
    }

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        SuspendLayout();
        //
        // LogViewerForm
        //
        ClientSize = new System.Drawing.Size(876, 639);
        ResumeLayout(false);
    }
}

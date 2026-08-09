using System.Windows.Forms;
using Microsoft.Win32;

namespace LlamaTray;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly ServerController _controller = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _animTimer;

    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _loadModelItem;
    private readonly ToolStripMenuItem _unloadAllItem;
    private readonly ToolStripMenuItem _settingsItem;

    private bool _busy;
    private bool _stopRequested;
    private ServerState _lastState = ServerState.Stopped;
    private Operation _activeOperation = Operation.None;
    private int _animFrame;
    private string? _lastLoadedModelId;
    private DateTime? _apiAnimationEndUtc;

    private DateTime? _lastActivityUtc;
    private string? _lastActivityModelId;
    private double? _lastDecodeTotal;

    // Status popup
    private readonly TooltipForm _statusPopup;
    private readonly System.Windows.Forms.Timer _popupHideTimer;
    private bool _popupVisible;
    private Dictionary<string, Dictionary<string, string>> _cachedPresets = new();
    private string? _presetsModelId; // cached presets are valid for this model id only
    private string? _lastLoadedModelIdForPopup; // track if model changed while popup is visible
    private Rectangle _menuBounds; // last-known menu screen bounds (captured in Opened event)

    private enum Operation
    {
        None,
        Loading,
        Unloading,
    }

    public TrayAppContext()
    {
        _headerItem = new ToolStripMenuItem("llama.cpp: checking...") { Enabled = false };
        _headerItem.Click += (_, _) => ShowStatusPopup();
        _startItem = new ToolStripMenuItem(
            "Start Server",
            null,
            async (_, _) => await RunAction(_controller.StartAsync, "Start")
        );
        _stopItem = new ToolStripMenuItem(
            "Stop Server",
            null,
            async (_, _) => await RunAction(_controller.StopAsync, "Stop")
        );
        _restartItem = new ToolStripMenuItem(
            "Restart Server",
            null,
            async (_, _) => await RunAction(_controller.RestartAsync, "Restart")
        );
        _loadModelItem = new ToolStripMenuItem("Load Model");
        _unloadAllItem = new ToolStripMenuItem(
            "Unload All Models",
            null,
            async (_, _) =>
                await RunAction(_controller.UnloadAllAsync, "Unload All", Operation.Unloading)
        );

        var openWebUiItem = new ToolStripMenuItem(
            "Open Web UI",
            null,
            (_, _) =>
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(ServerConfig.Current.BaseUrl)
                    {
                        UseShellExecute = true,
                    }
                )
        );

        var exitItem = new ToolStripMenuItem(
            "Exit",
            null,
            async (_, _) =>
            {
                await StopServerOnceAsync();
                ExitThread();
            }
        );

        _settingsItem = new ToolStripMenuItem(
            "Settings...",
            null,
            (_, _) => ShowSettingsDialogAsync()
        );

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            // Hide the status popup if it's visible — the user is opening the menu.
            if (_popupVisible)
                HideStatusPopup();
        };
        menu.Opened += (_, _) =>
        {
            // Capture the menu's screen bounds after layout is complete.
            // ContextMenuStrip.PointToScreen converts its (0,0) to screen coords.
            var topLeft = menu.PointToScreen(Point.Empty);
            _menuBounds = new Rectangle(topLeft, menu.Size);
        };

        menu.Items.Add(_headerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_loadModelItem);
        menu.Items.Add(_unloadAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openWebUiItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.Get(ServerState.Stopped),
            Text = "llama.cpp: checking...",
            ContextMenuStrip = menu,
            Visible = true,
        };

        RebuildLoadModelMenu(ModelCatalog.ReadModelIds(), currentlyLoadedId: null);

        _statusPopup = new TooltipForm();

        // Auto-hide the popup after a timeout.
        _popupHideTimer = new System.Windows.Forms.Timer { Interval = 500, Enabled = false };
        _popupHideTimer.Tick += (_, _) => HideStatusPopup();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += async (_, _) => await RefreshStateAsync();
        _pollTimer.Start();

        _animTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _animTimer.Tick += (_, _) => CycleAnimationFrame();

        SystemEvents.SessionEnding += OnSessionEnding;

        _ = RefreshStateAsync();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        StopServerOnceAsync().GetAwaiter().GetResult();
    }

    private async Task StopServerOnceAsync()
    {
        if (_stopRequested)
            return;
        _stopRequested = true;
        try
        {
            await _controller.StopAsync();
        }
        catch
        { /* best-effort on exit */
        }
    }

    private async Task RunAction(
        Func<Task<(bool ok, string message)>> action,
        string label,
        Operation op = Operation.None
    )
    {
        if (_busy)
            return;
        _busy = true;
        SetMenuEnabled(false);
        _activeOperation = op;
        _animFrame = 0;
        if (op != Operation.None)
            _animTimer.Start();
        try
        {
            var (ok, message) = await action();
            ShowBalloon($"{label}: {(ok ? "done" : "failed")}", message, ok);
        }
        catch (Exception ex)
        {
            ShowBalloon($"{label}: error", ex.Message, false);
        }
        finally
        {
            if (op != Operation.None)
            {
                _animTimer.Stop();
                _activeOperation = Operation.None;
            }
            _busy = false;
            await RefreshStateAsync();
        }
    }

    private async Task LoadModel(string modelId)
    {
        if (_busy)
            return;
        _busy = true;
        SetMenuEnabled(false);
        _activeOperation = Operation.Loading;
        _animFrame = 0;
        _animTimer.Start();
        try
        {
            var (ok, message) = await _controller.LoadModelAsync(modelId);
            ShowBalloon($"Load {modelId}: {(ok ? "done" : "failed")}", message, ok);
        }
        catch (Exception ex)
        {
            ShowBalloon("Load: error", ex.Message, false);
        }
        finally
        {
            _animTimer.Stop();
            _activeOperation = Operation.None;
            _busy = false;
            await RefreshStateAsync();
        }
    }

    /// <summary>
    /// Tracks per-model activity and unloads the model once it's been idle longer than
    /// AutoUnloadMinutes. Returns true if it triggered an unload (caller should bail out
    /// of the current refresh and let the next poll tick rebuild the UI from scratch).
    ///
    /// Activity is primarily detected via the cumulative n_decode_total counter from /metrics
    /// (requires "metrics = on" in the model's preset): any increase since the last poll means
    /// decoding happened at some point during that interval, however brief. /slots' "is_processing"
    /// is only a point-in-time snapshot taken every poll tick — a request that starts and fully
    /// completes between two ticks is invisible to it, so relying on it alone let short requests
    /// go undetected and the model got unloaded mid-session. is_processing is kept as a fallback
    /// for when /metrics is unavailable.
    /// </summary>
    private async Task<bool> CheckAutoUnloadAsync(string loadedId)
    {
        var timeoutMinutes = ServerConfig.Current.AutoUnloadMinutes;
        if (timeoutMinutes <= 0)
        {
            _lastActivityUtc = null;
            _lastActivityModelId = null;
            _lastDecodeTotal = null;
            return false;
        }

        if (loadedId != _lastActivityModelId)
        {
            _lastActivityModelId = loadedId;
            _lastActivityUtc = DateTime.UtcNow;
            _lastDecodeTotal = null;
        }

        var decodeTotal = await _controller.GetDecodeTotalAsync(loadedId);
        if (decodeTotal.HasValue)
        {
            if (_lastDecodeTotal.HasValue && decodeTotal.Value > _lastDecodeTotal.Value)
            {
                _lastActivityUtc = DateTime.UtcNow;
            }
            _lastDecodeTotal = decodeTotal;
        }
        else
        {
            // /metrics unavailable (e.g. "metrics = on" missing from the preset) — fall back to
            // the point-in-time /slots check, which can still miss short requests between polls.
            var busy = await _controller.IsModelBusyAsync(loadedId);
            if (busy == true)
            {
                _lastActivityUtc = DateTime.UtcNow;
            }
        }

        if (
            _lastActivityUtc == null
            || DateTime.UtcNow - _lastActivityUtc.Value < TimeSpan.FromMinutes(timeoutMinutes)
        )
        {
            return false;
        }

        _busy = true;
        SetMenuEnabled(false);
        _activeOperation = Operation.Unloading;
        _animFrame = 0;
        _animTimer.Start();
        try
        {
            var ok = await _controller.UnloadModelAsync(loadedId);
            if (ok)
                ShowBalloon(
                    "Auto-unload",
                    $"Unloaded '{loadedId}' after {timeoutMinutes} min of inactivity.",
                    true
                );
        }
        finally
        {
            _animTimer.Stop();
            _activeOperation = Operation.None;
            _lastActivityUtc = null;
            _lastActivityModelId = null;
            _lastDecodeTotal = null;
            _busy = false;
            await RefreshStateAsync();
        }

        return true;
    }

    private async Task RefreshStateAsync()
    {
        if (_busy)
        {
            SetMenuEnabled(false);
            return;
        }

        var listening = _controller.IsPortListening();
        ServerState state;
        string tooltip;
        string headerText;
        string? loadedId = null;
        List<ModelInfo>? models = null;
        double? vramGiB = null;

        if (!listening)
        {
            state = ServerState.Stopped;
            tooltip = "llama.cpp: stopped";
            headerText = "Stopped";
        }
        else
        {
            models = await _controller.GetModelsAsync();
            vramGiB = await Task.Run(_controller.GetVramUsageGiB);
            var vramSuffix = vramGiB.HasValue ? $" ({vramGiB.Value:0.0} GiB VRAM)" : "";

            var loaded = models?.FirstOrDefault(m => m.Status?.Value == "loaded");
            if (loaded != null)
            {
                state = ServerState.ModelLoaded;
                loadedId = loaded.Id;
                tooltip = $"llama.cpp: running{vramSuffix}";
                headerText = $"Running — {Truncate(loaded.Id, 40)}";
            }
            else
            {
                state = ServerState.StartedNoModel;
                tooltip = $"llama.cpp: running, no model loaded{vramSuffix}";
                headerText = "Running — no model loaded";
            }
        }

        if (state == ServerState.ModelLoaded && loadedId != null)
        {
            if (await CheckAutoUnloadAsync(loadedId))
                return;
        }
        else
        {
            _lastActivityUtc = null;
            _lastActivityModelId = null;
            _lastDecodeTotal = null;
        }

        var animIcon = GetMaybeAnimatedIcon(state);
        if (animIcon is not null)
            _notifyIcon.Icon = animIcon;
        else
            _notifyIcon.Icon = IconFactory.Get(state);
        // NotifyIcon.Text has a 63-char limit.
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

        // Update header text — add click hint when a model is loaded.
        _headerItem.Text =
            state == ServerState.ModelLoaded ? headerText + " (click for details)" : headerText;

        _startItem.Enabled = state == ServerState.Stopped;
        _stopItem.Enabled = state != ServerState.Stopped;
        _restartItem.Enabled = state != ServerState.Stopped;
        _unloadAllItem.Enabled = state == ServerState.ModelLoaded;
        _loadModelItem.Enabled = true;

        var ids =
            models?.Select(m => m.Id).Where(id => id != "default").ToList()
            ?? ModelCatalog.ReadModelIds();
        if (state != _lastState || _loadModelItem.DropDownItems.Count != ids.Count)
        {
            RebuildLoadModelMenu(ids, loadedId);
        }
        else
        {
            foreach (ToolStripMenuItem item in _loadModelItem.DropDownItems)
                item.Checked = string.Equals((string?)item.Tag, loadedId, StringComparison.Ordinal);
        }

        // Detect model-level transitions (API-initiated loads/swaps/unloads).
        // Only animate when not already busy — tray button actions start their own animation.
        if (!_busy)
        {
            var modelSwapped = loadedId != _lastLoadedModelId;
            var modelAppeared = _lastLoadedModelId == null && loadedId != null;
            var modelDisappeared = _lastLoadedModelId != null && loadedId == null;

            if (modelSwapped || modelAppeared || modelDisappeared)
            {
                _activeOperation = Operation.Loading;
                _animFrame = 0;
                _animTimer.Start();
                _apiAnimationEndUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            }
        }

        // Stop API animation once the 3 s grace window expires.
        if (_apiAnimationEndUtc.HasValue && DateTime.UtcNow >= _apiAnimationEndUtc.Value)
        {
            _animTimer.Stop();
            _activeOperation = Operation.None;
            _apiAnimationEndUtc = null;
        }

        // Enable/disable the header item based on model state.
        if (state == ServerState.ModelLoaded && loadedId != null)
        {
            _headerItem.Enabled = true;
            // Re-parse presets when the model id changes.
            if (loadedId != _presetsModelId)
            {
                _cachedPresets = IniParser.Parse(ServerConfig.Current.PresetIni);
                _presetsModelId = loadedId;
            }
            // If the popup is visible and the model changed underneath it (unlikely but possible
            // via external API call), update the popup content.
            if (_popupVisible && _lastLoadedModelIdForPopup != loadedId)
            {
                var info = BuildTooltipInfo();
                if (info != null)
                {
                    // Reposition relative to last known menu bounds.
                    var anchor = new Point(_menuBounds.Right + 4, _menuBounds.Y);
                    _statusPopup.ShowWith(info, anchor);
                }
                _lastLoadedModelIdForPopup = loadedId;
            }
        }
        else
        {
            _headerItem.Enabled = false;
            if (_popupVisible)
                HideStatusPopup();
        }

        _lastState = state;
        _lastLoadedModelId = loadedId;
    }

    /// <summary>Toggle the status popup on header click.</summary>
    private void ShowStatusPopup()
    {
        // Re-parse presets each time — the config may have changed since the popup was last shown.
        // The INI file is tiny so this is negligible overhead.
        if (_lastLoadedModelId != null)
        {
            _cachedPresets = IniParser.Parse(ServerConfig.Current.PresetIni);
            _presetsModelId = _lastLoadedModelId;
        }

        // Position the popup to the right of the context menu.
        // If that would go off-screen, put it above instead.
        var screen = Screen.PrimaryScreen!.Bounds;
        Point anchor;
        if (_menuBounds.Right + 200 <= screen.Right)
        {
            // Room to the right — place it there.
            anchor = new Point(_menuBounds.Right + 4, _menuBounds.Y);
        }
        else
        {
            // No room to the right — place it above the menu.
            anchor = new Point(_menuBounds.X, _menuBounds.Y - 4);
        }

        if (_popupVisible)
        {
            // Already visible — update with fresh data and extend timeout.
            var info = BuildTooltipInfo();
            if (info != null)
                _statusPopup.ShowWith(info, anchor);
            _popupHideTimer.Stop();
            _popupHideTimer.Interval = 6000;
            _popupHideTimer.Start();
        }
        else
        {
            var info = BuildTooltipInfo();
            if (info != null)
            {
                _statusPopup.ShowWith(info, anchor);
                _popupVisible = true;
                _lastLoadedModelIdForPopup = _lastLoadedModelId;
                _popupHideTimer.Interval = 6000;
                _popupHideTimer.Start();
            }
        }
    }

    /// <summary>Hide the status popup and reset state.</summary>
    private void HideStatusPopup()
    {
        _statusPopup.Hide();
        _popupVisible = false;
        _popupHideTimer.Stop();
        _lastLoadedModelIdForPopup = null;
    }

    /// <summary>
    /// Builds a ModelStatusInfo from the cached INI presets for the currently loaded model.
    /// Returns null if we don't have enough data.
    /// </summary>
    private ModelStatusInfo? BuildTooltipInfo()
    {
        if (_lastLoadedModelId == null || _lastState != ServerState.ModelLoaded)
            return null;

        var id = _lastLoadedModelId;
        var resolve = (string key) => IniParser.Resolve(_cachedPresets, id, key);

        // Format ctx-size with K/M suffix for readability.
        var rawCtx = resolve("ctx-size");
        var ctxDisplay =
            rawCtx != null && long.TryParse(rawCtx, out var ctxVal)
                ? FormatLargeNumber(ctxVal)
                : rawCtx;

        // Format batch/ubatch.
        var rawBatch = resolve("batch-size");
        var batchDisplay =
            rawBatch != null && long.TryParse(rawBatch, out var bVal)
                ? FormatLargeNumber(bVal)
                : rawBatch;

        var rawUbatch = resolve("ubatch-size");
        var ubatchDisplay =
            rawUbatch != null && long.TryParse(rawUbatch, out var uVal)
                ? FormatLargeNumber(uVal)
                : rawUbatch;

        // VRAM from live perf counter.
        var vramStr = _controller.GetVramUsageGiB()?.ToString("0.0");

        return new ModelStatusInfo(
            ModelId: id,
            CtxSize: ctxDisplay,
            BatchSize: batchDisplay,
            UbatchSize: ubatchDisplay,
            CacheTypeK: resolve("cache-type-k"),
            CacheTypeV: resolve("cache-type-v"),
            NgpuLayers: resolve("n-gpu-layers"),
            VramGiB: vramStr
        );
    }

    /// <summary>Format a large number with K/M suffix using binary units (e.g. 262144 → "256K").</summary>
    private static string FormatLargeNumber(long value)
    {
        if (value >= 1_048_576)
            return (value / 1048576) + "M";
        if (value >= 1024)
            return (value / 1024) + "K";
        return value.ToString();
    }

    private void RebuildLoadModelMenu(List<string> ids, string? currentlyLoadedId)
    {
        _loadModelItem.DropDownItems.Clear();
        if (ids.Count == 0)
        {
            _loadModelItem.DropDownItems.Add(
                new ToolStripMenuItem("(no models found)") { Enabled = false }
            );
            return;
        }

        foreach (var id in ids)
        {
            var item = new ToolStripMenuItem(Truncate(id, 40))
            {
                Tag = id,
                Checked = id == currentlyLoadedId,
            };
            item.Click += async (_, _) => await LoadModel(id);
            _loadModelItem.DropDownItems.Add(item);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] + "…" : text;

    private void SetMenuEnabled(bool enabled)
    {
        _startItem.Enabled = enabled && _lastState == ServerState.Stopped;
        _stopItem.Enabled = enabled && _lastState != ServerState.Stopped;
        _restartItem.Enabled = enabled && _lastState != ServerState.Stopped;
        _loadModelItem.Enabled = enabled;
        _unloadAllItem.Enabled = enabled && _lastState == ServerState.ModelLoaded;
    }

    private void ShowBalloon(string title, string text, bool isInfo)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
        _notifyIcon.BalloonTipIcon = isInfo ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>
    /// Advances the animation frame counter and updates the tray icon when an
    /// operation (load / unload) is in progress.
    /// </summary>
    private void CycleAnimationFrame()
    {
        _animFrame = (_animFrame + 1) % IconFactory.TotalAnimationFrames;
        var icon = GetMaybeAnimatedIcon(_lastState);
        if (icon is not null)
            _notifyIcon.Icon = icon;
    }

    private Icon? GetMaybeAnimatedIcon(ServerState state)
    {
        if (_activeOperation == Operation.None)
            return null;
        return IconFactory.GetAnimatedFrame(state, _animFrame, IconFactory.TotalAnimationFrames);
    }

    private async void ShowSettingsDialogAsync()
    {
        if (_busy)
            return;

        var before = ServerConfig.Current;
        var dialog = new SettingsForm(before);
        dialog.ShowDialog(null);

        if (dialog.DialogResult != DialogResult.OK || dialog.SavedConfig == null)
            return;

        var after = dialog.SavedConfig;

        bool needsRestart =
            after.Port != before.Port
            || after.ServerExe != before.ServerExe
            || after.ModelsDir != before.ModelsDir
            || after.PresetIni != before.PresetIni
            || after.MaxModels != before.MaxModels
            || after.StdOutLog != before.StdOutLog
            || after.StdErrLog != before.StdErrLog;

        if (needsRestart)
        {
            var result = MessageBox.Show(
                "Some settings require a server restart to take effect.\nRestart now?",
                "Restart Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result == DialogResult.Yes)
            {
                await RunAction(_controller.RestartAsync, "Restart");
            }
            else
            {
                ShowBalloon("Settings saved", "Restart the server to apply pending changes.", true);
            }
        }
        else
        {
            ShowBalloon("Settings saved", "Changes applied.", true);
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _animTimer.Stop();
        _animTimer.Dispose();
        _popupHideTimer.Stop();
        _popupHideTimer.Dispose();
        _statusPopup.Hide();
        _statusPopup.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}

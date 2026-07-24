using System.Windows.Forms;

namespace LlamaTray;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly ServerController _controller = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;

    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _loadModelItem;
    private readonly ToolStripMenuItem _unloadAllItem;

    private bool _busy;
    private ServerState _lastState = ServerState.Stopped;

    public TrayAppContext()
    {
        _headerItem = new ToolStripMenuItem("llama.cpp: checking...") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start Server", null, async (_, _) => await RunAction(_controller.StartAsync, "Start"));
        _stopItem = new ToolStripMenuItem("Stop Server", null, async (_, _) => await RunAction(_controller.StopAsync, "Stop"));
        _restartItem = new ToolStripMenuItem("Restart Server", null, async (_, _) => await RunAction(_controller.RestartAsync, "Restart"));
        _loadModelItem = new ToolStripMenuItem("Load Model");
        _unloadAllItem = new ToolStripMenuItem("Unload All Models", null, async (_, _) => await RunAction(_controller.UnloadAllAsync, "Unload All"));

        var openWebUiItem = new ToolStripMenuItem("Open Web UI", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ServerConfig.Current.BaseUrl) { UseShellExecute = true }));

        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
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
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.Get(ServerState.Stopped),
            Text = "llama.cpp: checking...",
            ContextMenuStrip = menu,
            Visible = true,
        };

        RebuildLoadModelMenu(ModelCatalog.ReadModelIds(), currentlyLoadedId: null);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += async (_, _) => await RefreshStateAsync();
        _pollTimer.Start();

        _ = RefreshStateAsync();
    }

    private async Task RunAction(Func<Task<(bool ok, string message)>> action, string label)
    {
        if (_busy) return;
        _busy = true;
        SetMenuEnabled(false);
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
            _busy = false;
            await RefreshStateAsync();
        }
    }

    private async Task LoadModel(string modelId)
    {
        if (_busy) return;
        _busy = true;
        SetMenuEnabled(false);
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
            _busy = false;
            await RefreshStateAsync();
        }
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

        if (!listening)
        {
            state = ServerState.Stopped;
            tooltip = "llama.cpp: stopped";
            headerText = "Stopped";
        }
        else
        {
            models = await _controller.GetModelsAsync();
            var loaded = models?.FirstOrDefault(m => m.Status?.Value == "loaded");
            if (loaded != null)
            {
                state = ServerState.ModelLoaded;
                loadedId = loaded.Id;
                tooltip = $"llama.cpp: {loaded.Id}";
                headerText = $"Running — {loaded.Id}";
            }
            else
            {
                state = ServerState.StartedNoModel;
                tooltip = "llama.cpp: running, no model loaded";
                headerText = "Running — no model loaded";
            }
        }

        _notifyIcon.Icon = IconFactory.Get(state);
        // NotifyIcon.Text has a 63-char limit.
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _headerItem.Text = headerText;

        _startItem.Enabled = state == ServerState.Stopped;
        _stopItem.Enabled = state != ServerState.Stopped;
        _restartItem.Enabled = state != ServerState.Stopped;
        _unloadAllItem.Enabled = state == ServerState.ModelLoaded;
        _loadModelItem.Enabled = true;

        var ids = models?.Select(m => m.Id).Where(id => id != "default").ToList()
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

        _lastState = state;
    }

    private void RebuildLoadModelMenu(List<string> ids, string? currentlyLoadedId)
    {
        _loadModelItem.DropDownItems.Clear();
        if (ids.Count == 0)
        {
            _loadModelItem.DropDownItems.Add(new ToolStripMenuItem("(no models found)") { Enabled = false });
            return;
        }

        foreach (var id in ids)
        {
            var item = new ToolStripMenuItem(id) { Tag = id, Checked = id == currentlyLoadedId };
            item.Click += async (_, _) => await LoadModel(id);
            _loadModelItem.DropDownItems.Add(item);
        }
    }

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

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}

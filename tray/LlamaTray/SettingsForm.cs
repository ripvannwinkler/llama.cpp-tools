using System.Windows.Forms;

namespace LlamaTray;

internal sealed class SettingsForm : Form
{
    private NumericUpDown _port;
    private TextBox _serverExe;
    private TextBox _modelsDir;
    private TextBox _presetIni;
    private NumericUpDown _maxModels;
    private NumericUpDown _autoUnloadMinutes;

    private readonly OpenFileDialog _fileDialog = new();
    private readonly FolderBrowserDialog _folderDialog = new();

    public AppConfig? SavedConfig => DialogResult == DialogResult.OK ? ReadConfig() : null;

    // LogFile is kept in AppConfig as a fallback for servers started outside the tray,
    // but isn't user-configurable here (the tray writes to a per-launch temp file).
    private readonly string _initialLogFile;

    public SettingsForm(AppConfig initial)
    {
        _initialLogFile = initial.LogFile;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LlamaTray Settings";
        ClientSize = new Size(800, 500);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(440, 340);

        _fileDialog.Filter = "Executable files|*.exe|All files|*.*";
        _fileDialog.FilterIndex = 1;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
            ColumnStyles =
            {
                new ColumnStyle(SizeType.AutoSize),
                new ColumnStyle(SizeType.Percent, 100),
                new ColumnStyle(SizeType.Absolute, 70),
            },
            Padding = new Padding(10),
            RowStyles =
            {
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
            },
        };

        Controls.Add(table);

        // Row 0: Port
        AddRow(table, 0, "Port:", new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = initial.Port,
            Dock = DockStyle.Fill,
        }, out _port);

        // Row 1: Server Exe
        AddRowWithBrowse(table, 1, "Server Exe:", browseIsFolder: false,
            new TextBox { Dock = DockStyle.Fill, Text = initial.ServerExe }, out _serverExe);

        // Row 2: Models Dir
        AddRowWithBrowse(table, 2, "Models Dir:", browseIsFolder: true,
            new TextBox { Dock = DockStyle.Fill, Text = initial.ModelsDir }, out _modelsDir);

        // Row 3: Preset Ini
        _fileDialog.Filter = "INI files|*.ini|All files|*.*";
        _fileDialog.FilterIndex = 1;
        AddRowWithBrowse(table, 3, "Preset Ini:", browseIsFolder: false,
            new TextBox { Dock = DockStyle.Fill, Text = initial.PresetIni }, out _presetIni);

        // Row 4: Max Models
        AddRow(table, 4, "Max Models:", new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = initial.MaxModels,
            Dock = DockStyle.Fill,
        }, out _maxModels);

        // Row 5: AutoUnload
        AddRow(table, 5, "AutoUnload (min, 0=off):", new NumericUpDown
        {
            Minimum = 0,
            Maximum = 99999,
            Value = initial.AutoUnloadMinutes,
            Dock = DockStyle.Fill,
        }, out _autoUnloadMinutes);

        // Buttons
        var btnPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(10, 5, 10, 10),
        };
        var btnTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            ColumnStyles =
            {
                new ColumnStyle(SizeType.Percent, 50),
                new ColumnStyle(SizeType.Percent, 50),
            },
        };

        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0),
        };
        Ui.StyleButton(btnOk);
        btnOk.Click += OnOk;

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Fill,
            Margin = new Padding(5, 0, 0, 0),
        };
        Ui.StyleButton(btnCancel);

        btnTable.Controls.Add(btnOk, 0, 0);
        btnTable.Controls.Add(btnCancel, 1, 0);
        btnPanel.Controls.Add(btnTable);
        Controls.Add(btnPanel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static void AddRow<T>(TableLayoutPanel table, int row, string labelText, T ctrl, out T field)
        where T : Control
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 5, 0),
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(ctrl, 1, row);
        table.SetColumnSpan(ctrl, 2);
        field = ctrl;
    }

    private void AddRowWithBrowse(
        TableLayoutPanel table,
        int row,
        string labelText,
        bool browseIsFolder,
        TextBox txt,
        out TextBox field)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 5, 0),
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(txt, 1, row);

        var btn = new Button
        {
            Text = "...",
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 0, 0, 0),
        };
        Ui.StyleButton(btn);

        btn.Click += (_, _) =>
        {
            if (browseIsFolder)
            {
                if (_folderDialog.ShowDialog(this) == DialogResult.OK)
                    txt.Text = _folderDialog.SelectedPath;
            }
            else
            {
                if (_fileDialog.ShowDialog(this) == DialogResult.OK)
                    txt.Text = _fileDialog.FileName;
            }
        };

        table.Controls.Add(btn, 2, row);
        field = txt;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        try
        {
            var config = ReadConfig();
            ServerConfig.SaveOverrides(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Error saving settings:\n{ex.Message}",
                "Save Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
        }
    }

    private AppConfig ReadConfig()
    {
        return new AppConfig
        {
            Port = (int)_port.Value,
            ServerExe = _serverExe.Text,
            ModelsDir = _modelsDir.Text,
            PresetIni = _presetIni.Text,
            LogFile = _initialLogFile,
            MaxModels = (int)_maxModels.Value,
            AutoUnloadMinutes = (int)_autoUnloadMinutes.Value,
        };
    }
}

using System.Windows.Forms;

namespace LlamaTray;

/// <summary>Small modal dialog that picks one of the configured model ids.</summary>
internal sealed class ModelPickerForm : Form
{
    public string? SelectedModel { get; private set; }

    public ModelPickerForm(IReadOnlyList<string> modelIds, string? currentModelId)
    {
        Text = "Load model";
        Icon = IconFactory.GetBaseIcon();
        Font = Ui.DefaultFont;
        BackColor = Ui.WindowDark;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.Scale(this, 440), Ui.Scale(this, 150));
        Padding = new Padding(Ui.Scale(this, Ui.OuterMargin));

        // Single-column table: label / combo / button row — all relative layout.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "Model",
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, Ui.Scale(this, Ui.Spacing)),
        };

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Ui.ButtonFace,
            ForeColor = Color.White,
        };
        foreach (var id in modelIds)
            combo.Items.Add(id);
        var currentIndex = currentModelId is null ? -1 : modelIds.ToList().IndexOf(currentModelId);
        combo.SelectedIndex = currentIndex >= 0 ? currentIndex : (modelIds.Count > 0 ? 0 : -1);

        // Two equal columns: both buttons stretch to the same width, 8px between.
        var halfGap = Ui.Scale(this, Ui.Spacing) / 2;
        var okButton = new Button
        {
            Text = "Load",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, halfGap, 0),
        };
        Ui.StyleButton(okButton);
        okButton.Click += (_, _) => SelectedModel = combo.SelectedItem as string;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(halfGap, 0, 0, 0),
        };
        Ui.StyleButton(cancelButton);

        var buttonPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, Ui.Scale(this, 12), 0, 0),
        };
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        buttonPanel.Controls.Add(okButton, 0, 0);
        buttonPanel.Controls.Add(cancelButton, 1, 0);

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(combo, 0, 1);
        layout.Controls.Add(buttonPanel, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.Add(layout);
    }
}

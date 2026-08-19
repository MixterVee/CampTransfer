namespace CampTransfer;

public sealed class DestinationNameDialog : Form
{
    private readonly TextBox _nameBox = new();

    private DestinationNameDialog(string path, string suggestedName)
    {
        Text = "Save Named Destination";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 145);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 0);
        _nameBox.Dock = DockStyle.Fill;
        _nameBox.Text = suggestedName;
        layout.Controls.Add(_nameBox, 1, 0);

        layout.Controls.Add(new Label { Text = "Path:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 1);
        var pathBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = path };
        layout.Controls.Add(pathBox, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 0)
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 2);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        };
    }

    public static bool TryGetName(IWin32Window owner, string path, string suggestedName, out string name)
    {
        using var dialog = new DestinationNameDialog(path, suggestedName);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            name = "";
            return false;
        }

        name = dialog._nameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return true;

        MessageBox.Show(owner, "Enter a name for this destination.", "Named Destination", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}

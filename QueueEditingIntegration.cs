using System.ComponentModel;

namespace CampTransfer;

internal static class QueueEditingIntegration
{
    public static void Attach(MainForm form)
    {
        var grid = FindControls<DataGridView>(form).FirstOrDefault();
        if (grid?.DataSource is not BindingSource bindingSource ||
            bindingSource.DataSource is not BindingList<TransferItem> queue)
        {
            return;
        }

        MakeModeColumnEditable(grid);
        AddRemoveAllButton(form, queue);
    }

    private static void MakeModeColumnEditable(DataGridView grid)
    {
        var existing = grid.Columns.Cast<DataGridViewColumn>()
            .FirstOrDefault(c => string.Equals(c.DataPropertyName, nameof(TransferItem.Operation), StringComparison.Ordinal));
        if (existing is null) return;

        var index = existing.Index;
        var width = existing.Width;
        var header = existing.HeaderText;
        grid.Columns.Remove(existing);

        var modeColumn = new DataGridViewComboBoxColumn
        {
            Name = "Mode",
            HeaderText = string.IsNullOrWhiteSpace(header) ? "Mode" : header,
            DataPropertyName = nameof(TransferItem.Operation),
            Width = Math.Max(78, width),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Standard,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        modeColumn.Items.AddRange("Copy", "Move");
        grid.Columns.Insert(index, modeColumn);

        // Keep every other queue field read-only; only Mode can be changed inline.
        grid.ReadOnly = false;
        foreach (DataGridViewColumn column in grid.Columns)
            column.ReadOnly = !ReferenceEquals(column, modeColumn);

        grid.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != modeColumn.Index || e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is not TransferItem item) return;

            if (!CanChangeMode(item))
            {
                e.Cancel = true;
                System.Media.SystemSounds.Beep.Play();
            }
        };

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.ColumnIndex == modeColumn.Index)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        grid.DataError += (_, e) =>
        {
            if (e.ColumnIndex == modeColumn.Index)
                e.ThrowException = false;
        };
    }

    private static bool CanChangeMode(TransferItem item)
    {
        if (item.Completed || item.SourceCleanupPending || item.ProgressPercent > 0.001)
            return false;

        return item.Status is not "Transferring" and not "Paused" and not "Resuming" and not "Deleting source" &&
               !item.Status.StartsWith("Retrying", StringComparison.Ordinal);
    }

    private static void AddRemoveAllButton(MainForm form, BindingList<TransferItem> queue)
    {
        var toolbar = FindControls<FlowLayoutPanel>(form)
            .FirstOrDefault(p => p.Controls.OfType<Button>().Any(b => b.Text == "Remove"));
        if (toolbar is null) return;

        var removeButton = toolbar.Controls.OfType<Button>()
            .FirstOrDefault(b => b.Text == "Remove");
        if (removeButton is null) return;

        var removeAllButton = new Button
        {
            Text = "Remove All",
            AutoSize = true,
            Enabled = queue.Count > 0
        };

        toolbar.Controls.Add(removeAllButton);
        var removeIndex = toolbar.Controls.GetChildIndex(removeButton);
        toolbar.Controls.SetChildIndex(removeAllButton, Math.Min(removeIndex + 1, toolbar.Controls.Count - 1));

        removeAllButton.Click += (_, _) =>
        {
            if (queue.Count == 0) return;
            if (HasActiveTransfer(queue))
            {
                MessageBox.Show(form,
                    "Remove All is unavailable while a transfer is active.",
                    "CampTransfer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var fileWord = queue.Count == 1 ? "item" : "items";
            var answer = MessageBox.Show(form,
                $"Remove all {queue.Count} queued {fileWord}?",
                "Remove All",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            queue.Clear();
        };

        var timer = new System.Windows.Forms.Timer { Interval = 400 };
        timer.Tick += (_, _) =>
        {
            removeAllButton.Enabled = queue.Count > 0 && !HasActiveTransfer(queue);
        };
        timer.Start();

        form.FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
        };
    }

    private static bool HasActiveTransfer(IEnumerable<TransferItem> queue) => queue.Any(item =>
        item.Status is "Transferring" or "Paused" or "Resuming" or "Deleting source" ||
        item.Status.StartsWith("Retrying", StringComparison.Ordinal));

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}

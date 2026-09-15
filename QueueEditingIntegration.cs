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

        MakeModeColumnEditable(grid, bindingSource);
        AddColumnSorting(grid, queue);
        AddRemoveAllButton(form, queue);
    }

    private static void MakeModeColumnEditable(DataGridView grid, BindingSource bindingSource)
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
            SortMode = DataGridViewColumnSortMode.Programmatic
        };
        modeColumn.Items.AddRange("Copy", "Move");
        grid.Columns.Insert(index, modeColumn);

        // Keep every other queue field read-only; only Mode can be changed inline.
        grid.ReadOnly = false;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        foreach (DataGridViewColumn column in grid.Columns)
            column.ReadOnly = !ReferenceEquals(column, modeColumn);

        var modeEditActive = false;

        grid.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != modeColumn.Index || e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].DataBoundItem is not TransferItem item) return;

            if (!CanChangeMode(item))
            {
                e.Cancel = true;
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            // MainForm refreshes the current row several times per second while a
            // transfer is active. Those refreshes were resetting the ComboBox editor
            // back to its old value while the user was trying to choose Copy/Move.
            // Suppress BindingSource notifications only for the brief edit session.
            modeEditActive = true;
            bindingSource.RaiseListChangedEvents = false;
        };

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.ColumnIndex == modeColumn.Index)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        grid.CellEndEdit += (_, e) =>
        {
            if (!modeEditActive || e.ColumnIndex != modeColumn.Index) return;

            modeEditActive = false;
            bindingSource.RaiseListChangedEvents = true;
            bindingSource.EndEdit();
            bindingSource.ResetCurrentItem();
        };

        grid.DataError += (_, e) =>
        {
            if (e.ColumnIndex == modeColumn.Index)
                e.ThrowException = false;
        };

        grid.Disposed += (_, _) =>
        {
            if (modeEditActive)
                bindingSource.RaiseListChangedEvents = true;
        };
    }

    private static bool CanChangeMode(TransferItem item)
    {
        // Copy/Move only changes what happens after the destination file is complete,
        // so it is safe to change even while data is transferring or a resumable
        // partial exists. Once source deletion has started, however, it is too late.
        if (item.Completed || item.SourceCleanupPending)
            return false;

        return !item.Status.StartsWith("Deleting source", StringComparison.Ordinal);
    }

    private static void AddColumnSorting(DataGridView grid, BindingList<TransferItem> queue)
    {
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;

        string? sortedProperty = null;
        var ascending = true;

        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count) return;

            var column = grid.Columns[e.ColumnIndex];
            var property = column.DataPropertyName;
            if (string.IsNullOrWhiteSpace(property)) return;

            if (string.Equals(sortedProperty, property, StringComparison.Ordinal))
                ascending = !ascending;
            else
            {
                sortedProperty = property;
                ascending = true;
            }

            var selectedIds = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.DataBoundItem as TransferItem)
                .Where(i => i is not null)
                .Select(i => i!.Id)
                .ToHashSet();

            IEnumerable<TransferItem> ordered = property switch
            {
                nameof(TransferItem.FileName) => ascending
                    ? queue.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
                    : queue.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase),
                nameof(TransferItem.Operation) => ascending
                    ? queue.OrderBy(i => i.Operation, StringComparer.OrdinalIgnoreCase)
                    : queue.OrderByDescending(i => i.Operation, StringComparer.OrdinalIgnoreCase),
                nameof(TransferItem.DestinationDisplay) => ascending
                    ? queue.OrderBy(i => i.DestinationDisplay, StringComparer.OrdinalIgnoreCase)
                    : queue.OrderByDescending(i => i.DestinationDisplay, StringComparer.OrdinalIgnoreCase),
                nameof(TransferItem.SizeText) => ascending
                    ? queue.OrderBy(i => i.SizeBytes)
                    : queue.OrderByDescending(i => i.SizeBytes),
                nameof(TransferItem.ProgressText) => ascending
                    ? queue.OrderBy(i => i.ProgressPercent)
                    : queue.OrderByDescending(i => i.ProgressPercent),
                nameof(TransferItem.Speed) => ascending
                    ? queue.OrderBy(i => i.CurrentBytesPerSecond)
                    : queue.OrderByDescending(i => i.CurrentBytesPerSecond),
                nameof(TransferItem.Eta) => ascending
                    ? queue.OrderBy(i => i.Eta, StringComparer.OrdinalIgnoreCase)
                    : queue.OrderByDescending(i => i.Eta, StringComparer.OrdinalIgnoreCase),
                nameof(TransferItem.Status) => ascending
                    ? queue.OrderBy(i => i.Status, StringComparer.OrdinalIgnoreCase)
                    : queue.OrderByDescending(i => i.Status, StringComparer.OrdinalIgnoreCase),
                _ => queue
            };

            var sorted = ordered.ToList();
            queue.RaiseListChangedEvents = false;
            try
            {
                queue.Clear();
                foreach (var item in sorted)
                    queue.Add(item);
            }
            finally
            {
                queue.RaiseListChangedEvents = true;
                queue.ResetBindings();
            }

            foreach (DataGridViewColumn other in grid.Columns)
                other.HeaderCell.SortGlyphDirection = SortOrder.None;
            column.HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;

            grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.DataBoundItem is TransferItem item && selectedIds.Contains(item.Id))
                    row.Selected = true;
            }
        };
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
                    "Turtle Transfer",
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

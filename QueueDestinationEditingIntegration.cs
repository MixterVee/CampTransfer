namespace CampTransfer;

internal static class QueueDestinationEditingIntegration
{
    public static void Attach(MainForm form)
    {
        var grid = FindControls<DataGridView>(form).FirstOrDefault();
        if (grid?.DataSource is not BindingSource bindingSource ||
            bindingSource.DataSource is not System.ComponentModel.BindingList<TransferItem> queue ||
            grid.ContextMenuStrip is null)
        {
            return;
        }

        var changeDestinationItem = new ToolStripMenuItem("Change Destination...");
        changeDestinationItem.Click += (_, _) => ChangeDestination(form, grid, queue);

        // Put destination actions together near the top of the queue context menu.
        var insertIndex = Math.Min(1, grid.ContextMenuStrip.Items.Count);
        grid.ContextMenuStrip.Items.Insert(insertIndex, changeDestinationItem);
    }

    private static void ChangeDestination(
        MainForm form,
        DataGridView grid,
        System.ComponentModel.BindingList<TransferItem> queue)
    {
        var selected = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as TransferItem)
            .Where(i => i is not null)
            .Cast<TransferItem>()
            .ToList();

        if (selected.Count == 0) return;

        var editable = selected.Where(CanChangeDestination).ToList();
        if (editable.Count == 0)
        {
            MessageBox.Show(
                form,
                "The selected item cannot have its destination changed while it is transferring or after transfer progress has started.",
                "Change Destination",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var current = editable
            .Select(i => PathHelpers.NormalizeDestinationPath(i.DestinationRoot))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

        using var dialog = new FolderBrowserDialog
        {
            Description = editable.Count == 1
                ? $"Choose a new destination for {editable[0].FileName}"
                : $"Choose a new destination for {editable.Count} selected files",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(current) ? current : ""
        };

        if (dialog.ShowDialog(form) != DialogResult.OK) return;

        var destination = PathHelpers.NormalizeDestinationPath(dialog.SelectedPath);
        if (string.IsNullOrWhiteSpace(destination)) return;

        foreach (var item in editable)
        {
            item.DestinationRoot = destination;
            item.Completed = false;
            item.ProgressPercent = 0;
            item.Status = File.Exists(item.SourcePath) ? "Queued" : "Source missing";
            item.NotifyDestinationChanged();
        }

        grid.Refresh();
        try { AppSettings.SaveQueue(queue); } catch { }
    }

    private static bool CanChangeDestination(TransferItem item)
    {
        if (item.Completed || item.SourceCleanupPending || item.ProgressPercent > 0.001)
            return false;

        return item.Status is not "Transferring" and not "Paused" and not "Resuming" and not "Deleting source" &&
               !item.Status.StartsWith("Retrying", StringComparison.Ordinal);
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}

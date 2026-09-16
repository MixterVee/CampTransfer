using System.Reflection;

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

        var engine = typeof(MainForm)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(form) as TransferEngine;
        if (engine is null) return;

        var changeDestinationItem = new ToolStripMenuItem("Change Destination...");
        changeDestinationItem.Click += async (_, _) => await ChangeDestinationAsync(form, grid, queue, engine);

        var insertIndex = Math.Min(1, grid.ContextMenuStrip.Items.Count);
        grid.ContextMenuStrip.Items.Insert(insertIndex, changeDestinationItem);
    }

    private static async Task ChangeDestinationAsync(
        MainForm form,
        DataGridView grid,
        System.ComponentModel.BindingList<TransferItem> queue,
        TransferEngine engine)
    {
        var selected = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as TransferItem)
            .Where(i => i is not null)
            .Cast<TransferItem>()
            .ToList();

        if (selected.Count == 0) return;

        var pausedActive = selected.Where(item => IsPausedActiveTransfer(item, engine)).ToList();
        if (pausedActive.Count > 0)
        {
            if (selected.Count != 1 || pausedActive.Count != 1)
            {
                MessageBox.Show(
                    form,
                    "Change the destination of a paused active transfer by itself. Queued files can still be changed together.",
                    "Change Destination",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            await ChangePausedActiveDestinationAsync(form, grid, queue, engine, pausedActive[0]);
            return;
        }

        var editable = selected.Where(CanChangeQueuedDestination).ToList();
        if (editable.Count == 0)
        {
            MessageBox.Show(
                form,
                "Pause the active transfer before changing its destination. Queued files that have not started can be changed at any time.",
                "Change Destination",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (editable.Count != selected.Count)
        {
            MessageBox.Show(
                form,
                "One or more selected files have already started. Change those individually while they are paused.",
                "Change Destination",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var current = editable
            .Select(i => PathHelpers.NormalizeDestinationPath(i.DestinationRoot))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

        using var dialog = CreateDestinationDialog(
            current,
            editable.Count == 1
                ? $"Choose a new destination for {editable[0].FileName}"
                : $"Choose a new destination for {editable.Count} selected files");

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

    private static async Task ChangePausedActiveDestinationAsync(
        MainForm form,
        DataGridView grid,
        System.ComponentModel.BindingList<TransferItem> queue,
        TransferEngine engine,
        TransferItem item)
    {
        var current = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
        using var dialog = CreateDestinationDialog(
            current,
            $"Choose a new destination for paused transfer {item.FileName}");

        if (dialog.ShowDialog(form) != DialogResult.OK) return;

        var destination = PathHelpers.NormalizeDestinationPath(dialog.SelectedPath);
        if (string.IsNullOrWhiteSpace(destination) ||
            string.Equals(destination, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previousStatus = item.Status;
        item.Status = "Moving partial to new destination";
        item.Speed = "";
        item.Eta = "";
        grid.Refresh();

        TransferEngine.DestinationChangeResult result;
        try
        {
            result = await engine.ChangePausedDestinationAsync(item, destination);
        }
        catch (Exception ex)
        {
            result = new TransferEngine.DestinationChangeResult(false, ex.Message);
        }

        if (result.Ok)
        {
            if (engine.IsPaused)
                item.Status = "Paused";
            grid.Refresh();
            try { AppSettings.SaveQueue(queue); } catch { }

            MessageBox.Show(
                form,
                "The partial transfer was moved to the new destination.\n\nPress Resume when you're ready to continue from the same progress.",
                "Destination Changed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            item.Status = engine.IsPaused ? "Paused" : previousStatus;
            grid.Refresh();
            MessageBox.Show(
                form,
                result.Message,
                "Destination Not Changed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static FolderBrowserDialog CreateDestinationDialog(string current, string description) => new()
    {
        Description = description,
        UseDescriptionForTitle = true,
        ShowNewFolderButton = true,
        InitialDirectory = Directory.Exists(current) ? current : ""
    };

    private static bool IsPausedActiveTransfer(TransferItem item, TransferEngine engine)
    {
        return engine.IsRunning &&
               engine.IsPaused &&
               item.ProgressPercent > 0.001 &&
               item.Status is "Paused" or "Moving partial to new destination";
    }

    private static bool CanChangeQueuedDestination(TransferItem item)
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

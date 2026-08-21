using System.ComponentModel;

namespace CampTransfer;

internal static class ActiveCopyToMove
{
    public static void Attach(MainForm form)
    {
        var grid = FindControls<DataGridView>(form).FirstOrDefault();
        var setMoveItem = grid?.ContextMenuStrip?.Items
            .OfType<ToolStripMenuItem>()
            .FirstOrDefault(item => string.Equals(item.Text, "Set Selected to Move", StringComparison.Ordinal));

        if (grid is null || setMoveItem is null)
            return;

        setMoveItem.Click += (_, _) =>
        {
            if (grid.DataSource is not BindingSource bindingSource ||
                bindingSource.DataSource is not BindingList<TransferItem> queue)
            {
                return;
            }

            var changed = false;

            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                if (row.DataBoundItem is not TransferItem item ||
                    item.Completed ||
                    item.SourceCleanupPending ||
                    !string.Equals(item.Operation, "Copy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var activeTransfer =
                    item.Status is "Transferring" or "Paused" or "Resuming" ||
                    item.Status.StartsWith("Retrying", StringComparison.Ordinal);

                if (!activeTransfer)
                    continue;

                // TransferEngine checks Operation after the destination has been
                // finalized, so changing an in-flight Copy to Move is safe: the
                // current copy continues uninterrupted and source cleanup runs at
                // completion using the normal Move safety rules.
                item.Operation = "Move";
                changed = true;
            }

            if (!changed)
                return;

            grid.Refresh();
            try { AppSettings.SaveQueue(queue); } catch { }
        };
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
                yield return match;

            foreach (var nested in FindControls<T>(child))
                yield return nested;
        }
    }
}

using System.ComponentModel;

namespace CampTransfer;

internal static class DestinationQueueSync
{
    public static void Attach(MainForm form)
    {
        var destinationBox = FindControls<ComboBox>(form)
            .FirstOrDefault(c => c.Dock == DockStyle.Fill && c.DropDownStyle == ComboBoxStyle.DropDown);
        var grid = FindControls<DataGridView>(form).FirstOrDefault();

        if (destinationBox is null || grid?.DataSource is not BindingSource bindingSource)
            return;
        if (bindingSource.DataSource is not BindingList<TransferItem> queue)
            return;

        var lastCommittedPath = PathHelpers.NormalizeDestinationPath(destinationBox.Text);

        destinationBox.TextChanged += (_, _) =>
        {
            var newPath = PathHelpers.NormalizeDestinationPath(destinationBox.Text);
            if (string.IsNullOrWhiteSpace(newPath) ||
                string.Equals(newPath, lastCommittedPath, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(newPath))
            {
                return;
            }

            var oldPath = lastCommittedPath;
            lastCommittedPath = newPath;

            if (string.IsNullOrWhiteSpace(oldPath))
                return;

            var changed = false;
            foreach (var item in queue)
            {
                if (item.Completed ||
                    item.Status is "Transferring" or "Paused" or "Resuming" ||
                    item.Status.StartsWith("Retrying", StringComparison.Ordinal))
                {
                    continue;
                }

                var itemDestination = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
                if (!string.Equals(itemDestination, oldPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                item.DestinationRoot = newPath;
                item.Completed = false;
                item.Status = File.Exists(item.SourcePath) ? "Queued" : "Source missing";
                item.NotifyDestinationChanged();
                changed = true;
            }

            if (changed)
                grid.Refresh();
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

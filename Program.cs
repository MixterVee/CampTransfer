namespace CampTransfer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Keep the original mutex name so upgraded CampTransfer/Turtle Transfer builds
        // cannot accidentally run side-by-side and fight over the monitor port.
        using var singleInstance = new Mutex(true, @"Local\CampTransfer.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Turtle Transfer is already running. Check the Windows system tray to reopen it.",
                "Turtle Transfer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // MainForm's controls raise TextChanged while RestoreSettings is still filling
        // the other controls. In older builds that could overwrite the saved upload
        // limit with the default 2 Mbps before the speed box itself was restored.
        // Capture the persisted value first, then put it back after construction.
        var rememberedUploadLimit = AppSettings.Load().SpeedLimitText;

        var form = new MainForm();
        RestoreRememberedUploadLimit(form, rememberedUploadLimit);
        BrandingIntegration.Apply(form);
        StartButtonAccent.Apply(form);
        DestinationQueueSync.Attach(form);
        QueueEditingIntegration.Attach(form);
        RemoteControlIntegration.Attach(form);
        RemoteMonitorIntegration.Attach(form);
        RemoteRelayIntegration.Attach(form);
        MainWindowSafetyIntegration.Attach(form);
        Application.Run(form);
    }

    private static void RestoreRememberedUploadLimit(Control root, string? rememberedUploadLimit)
    {
        if (string.IsNullOrWhiteSpace(rememberedUploadLimit) ||
            !SpeedParser.TryParse(rememberedUploadLimit, out _))
            return;

        foreach (Control child in root.Controls)
        {
            if (child is ComboBox combo &&
                combo.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "Unlimited", StringComparison.OrdinalIgnoreCase)) &&
                combo.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "0.25 Mbps", StringComparison.OrdinalIgnoreCase)))
            {
                combo.Text = rememberedUploadLimit.Trim();
                return;
            }

            RestoreRememberedUploadLimit(child, rememberedUploadLimit);
        }
    }
}

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

        var form = new MainForm();
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
}

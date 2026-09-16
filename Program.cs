namespace CampTransfer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

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
        TurtleBranding.Apply(form);
        Application.Run(form);
    }
}

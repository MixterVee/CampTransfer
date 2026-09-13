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
                "CampTransfer is already running. Check the Windows system tray to reopen it.",
                "CampTransfer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var form = new MainForm();
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

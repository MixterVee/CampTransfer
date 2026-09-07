namespace CampTransfer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var form = new MainForm();
        StartButtonAccent.Apply(form);
        DestinationQueueSync.Attach(form);
        QueueEditingIntegration.Attach(form);
        RemoteMonitorIntegration.Attach(form);
        RemoteRelayIntegration.Attach(form);
        Application.Run(form);
    }
}

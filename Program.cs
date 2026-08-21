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
        ActiveCopyToMove.Attach(form);
        Application.Run(form);
    }
}

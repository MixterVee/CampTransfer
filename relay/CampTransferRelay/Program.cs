namespace CampTransferRelay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startInTray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new RelayForm(startInTray));
    }
}

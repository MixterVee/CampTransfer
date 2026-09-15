namespace CampTransfer;

internal static class BrandingIntegration
{
    public static void Apply(MainForm form)
    {
        form.Text = "Turtle Transfer";
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
                form.Icon = icon;
        }
        catch
        {
            // Branding should never prevent the transfer app from starting.
        }
    }
}

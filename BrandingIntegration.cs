using System.Reflection;

namespace CampTransfer;

internal static class BrandingIntegration
{
    public static void Apply(MainForm form)
    {
        form.Text = "Turtle Transfer";
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is null) return;

            form.Icon = icon;

            var notifyField = typeof(MainForm).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
            if (notifyField?.GetValue(form) is NotifyIcon notifyIcon)
                notifyIcon.Icon = icon;
        }
        catch
        {
            // Artwork should never prevent Turtle Transfer from starting.
        }
    }
}

namespace CampTransfer;

internal static class TurtleBranding
{
    private const string OldBrand = "CampTransfer";
    private const string NewBrand = "Turtle Transfer";

    public static void Apply(Form mainForm)
    {
        ReplaceForm(mainForm);
        Application.Idle += (_, _) =>
        {
            foreach (Form form in Application.OpenForms)
                ReplaceForm(form);
        };
    }

    private static void ReplaceForm(Form form)
    {
        ReplaceControl(form);
        if (form.MainMenuStrip is not null)
            ReplaceToolStrip(form.MainMenuStrip);
    }

    private static void ReplaceControl(Control control)
    {
        if (!string.IsNullOrEmpty(control.Text) && control.Text.Contains(OldBrand, StringComparison.Ordinal))
            control.Text = control.Text.Replace(OldBrand, NewBrand, StringComparison.Ordinal);

        if (control.ContextMenuStrip is not null)
            ReplaceToolStrip(control.ContextMenuStrip);

        if (control is ToolStrip strip)
            ReplaceToolStrip(strip);

        foreach (Control child in control.Controls)
            ReplaceControl(child);
    }

    private static void ReplaceToolStrip(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items)
            ReplaceToolStripItem(item);
    }

    private static void ReplaceToolStripItem(ToolStripItem item)
    {
        if (!string.IsNullOrEmpty(item.Text) && item.Text.Contains(OldBrand, StringComparison.Ordinal))
            item.Text = item.Text.Replace(OldBrand, NewBrand, StringComparison.Ordinal);

        if (item is ToolStripDropDownItem dropDown)
        {
            foreach (ToolStripItem child in dropDown.DropDownItems)
                ReplaceToolStripItem(child);
        }
    }
}

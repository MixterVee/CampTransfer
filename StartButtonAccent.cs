namespace CampTransfer;

internal static class StartButtonAccent
{
    public static void Apply(Form form)
    {
        var startButton = FindStartButton(form.Controls);
        if (startButton is null) return;

        startButton.UseVisualStyleBackColor = false;
        startButton.BackColor = Color.FromArgb(205, 245, 248);
        startButton.ForeColor = Color.Black;
    }

    private static Button? FindStartButton(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (control is Button button && button.Text == "Start")
                return button;

            var nested = FindStartButton(control.Controls);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}

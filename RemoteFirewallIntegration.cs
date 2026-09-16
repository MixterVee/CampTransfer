using System.Diagnostics;

namespace CampTransfer;

internal static class RemoteFirewallIntegration
{
    private const string TcpRule = "Turtle Transfer Remote Monitor TCP";
    private const string UdpRule = "Turtle Transfer Discovery UDP";

    public static void Ensure(MainForm form)
    {
        form.Shown += (_, _) =>
        {
            try
            {
                form.BeginInvoke((Action)(() => EnsureAfterShown(form)));
            }
            catch { }
        };
    }

    private static void EnsureAfterShown(Form form)
    {
        try
        {
            if (HasRule(TcpRule) && HasRule(UdpRule)) return;

            var answer = MessageBox.Show(
                form,
                "Turtle Transfer Remote Monitor needs access through Windows Firewall so your phone can see live transfers.\n\nAdd the required Private-network firewall rules now?\n\nWindows will ask for administrator approval once.",
                "Enable Turtle Transfer Remote Monitor",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);

            if (answer != DialogResult.Yes) return;

            if (InstallRules())
            {
                MessageBox.Show(
                    form,
                    "Remote Monitor firewall access is enabled. Turtle Transfer Remote should connect automatically within a few seconds.",
                    "Turtle Transfer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    form,
                    "Windows Firewall access was not changed. Turtle Transfer itself will keep working, but the Remote app may remain offline until TCP 45827 and UDP 45828 are allowed on Private networks.",
                    "Turtle Transfer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch
        {
            // Firewall setup is only for remote access and must never prevent the transfer app from running.
        }
    }

    private static bool HasRule(string displayName)
    {
        try
        {
            var script = $"if (Get-NetFirewallRule -DisplayName '{EscapePowerShell(displayName)}' -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            if (process is null) return false;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool InstallRules()
    {
        try
        {
            var script =
                "$ErrorActionPreference='Stop'; " +
                $"Get-NetFirewallRule -DisplayName '{EscapePowerShell(TcpRule)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
                $"Get-NetFirewallRule -DisplayName '{EscapePowerShell(UdpRule)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
                $"New-NetFirewallRule -DisplayName '{EscapePowerShell(TcpRule)}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45827 -Profile Private | Out-Null; " +
                $"New-NetFirewallRule -DisplayName '{EscapePowerShell(UdpRule)}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45828 -Profile Private | Out-Null;";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

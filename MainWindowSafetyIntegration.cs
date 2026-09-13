using System.ComponentModel;
using System.Text.Json;

namespace CampTransfer;

internal static class MainWindowSafetyIntegration
{
    public static void Attach(MainForm form)
    {
        _ = new Controller(form);
    }

    private sealed class Controller
    {
        private readonly MainForm _form;
        private readonly NotifyIcon _trayIcon;
        private readonly CloseMessageFilter _closeFilter;
        private bool _shownTrayNotice;

        public Controller(MainForm form)
        {
            _form = form;
            _trayIcon = BuildTrayIcon();
            _closeFilter = new CloseMessageFilter(form, HideToTray);
            Application.AddMessageFilter(_closeFilter);

            _form.Shown += (_, _) =>
            {
                try { _form.BeginInvoke((Action)OfferResumePreviousTransfer); }
                catch { }
            };

            _form.FormClosed += (_, _) =>
            {
                Application.RemoveMessageFilter(_closeFilter);
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            };
        }

        private NotifyIcon BuildTrayIcon()
        {
            var menu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("Open CampTransfer");
            openItem.Click += (_, _) => RestoreFromTray();
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit CampTransfer");
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            var icon = new NotifyIcon
            {
                Text = "CampTransfer",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = false
            };
            icon.DoubleClick += (_, _) => RestoreFromTray();
            return icon;
        }

        private void HideToTray()
        {
            if (_form.IsDisposed || !_form.Visible) return;

            _form.ShowInTaskbar = false;
            _form.Hide();
            _trayIcon.Visible = true;

            if (_shownTrayNotice) return;
            _shownTrayNotice = true;
            _trayIcon.BalloonTipTitle = "CampTransfer is still running";
            _trayIcon.BalloonTipText = "The window was hidden so transfers can continue. Double-click the tray icon to reopen CampTransfer.";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }

        private void RestoreFromTray()
        {
            if (_form.IsDisposed) return;

            _trayIcon.Visible = false;
            _form.ShowInTaskbar = true;
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }

        private void ExitApplication()
        {
            if (_form.IsDisposed) return;

            if (IsTransferActive())
            {
                var answer = MessageBox.Show(
                    _form,
                    "A transfer is currently active. Exiting will stop it, although CampTransfer can resume valid partial data the next time it starts.\n\nExit CampTransfer anyway?",
                    "Transfer in progress",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }

            Application.RemoveMessageFilter(_closeFilter);
            _trayIcon.Visible = false;
            _form.Close();
        }

        private bool IsTransferActive()
        {
            var queue = GetQueue();
            if (queue is null) return false;

            return queue.Any(item =>
                item.Status is "Transferring" or "Paused" or "Resuming" ||
                item.Status.StartsWith("Retrying", StringComparison.Ordinal) ||
                item.Status.StartsWith("Deleting source", StringComparison.Ordinal));
        }

        private void OfferResumePreviousTransfer()
        {
            var queue = GetQueue();
            if (queue is null || queue.Count == 0) return;

            var resumable = new List<ResumeCandidate>();
            foreach (var item in queue.Where(i => !i.Completed))
            {
                if (item.SourceCleanupPending)
                {
                    resumable.Add(new ResumeCandidate(item, 0, item.SizeBytes, CleanupOnly: true));
                    continue;
                }

                if (TryGetResumeBytes(item, out var resumeBytes, out var sourceLength))
                {
                    item.Status = "Resume available";
                    item.ProgressPercent = sourceLength <= 0 ? 100 : (double)resumeBytes / sourceLength * 100;
                    resumable.Add(new ResumeCandidate(item, resumeBytes, sourceLength, CleanupOnly: false));
                }
            }

            if (resumable.Count == 0) return;

            try { AppSettings.SaveQueue(queue); } catch { }

            var partials = resumable.Where(r => !r.CleanupOnly).ToList();
            var cleanups = resumable.Count - partials.Count;
            string details;

            if (resumable.Count == 1 && partials.Count == 1)
            {
                var candidate = partials[0];
                var percent = candidate.SourceLength <= 0
                    ? 100
                    : (double)candidate.ResumeBytes / candidate.SourceLength * 100;
                details = $"{candidate.Item.FileName} is {percent:0.0}% complete and can continue from the existing partial file.";
            }
            else
            {
                var parts = new List<string>();
                if (partials.Count > 0)
                    parts.Add($"{partials.Count} partial {(partials.Count == 1 ? "file can" : "files can")} continue from existing data");
                if (cleanups > 0)
                    parts.Add($"{cleanups} interrupted Move cleanup {(cleanups == 1 ? "needs" : "need")} to finish");
                details = string.Join("; ", parts) + ".";
            }

            var answer = MessageBox.Show(
                _form,
                $"CampTransfer found unfinished work from the previous session.\n\n{details}\n\nResume the previous transfer now?",
                "Resume previous transfer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (answer != DialogResult.Yes) return;

            var startButton = FindControls<Button>(_form)
                .FirstOrDefault(b => string.Equals(b.Text, "Start", StringComparison.Ordinal));
            if (startButton?.Enabled == true)
                startButton.PerformClick();
        }

        private BindingList<TransferItem>? GetQueue()
        {
            var grid = FindControls<DataGridView>(_form).FirstOrDefault();
            if (grid?.DataSource is not BindingSource source)
                return null;
            return source.DataSource as BindingList<TransferItem>;
        }

        private static bool TryGetResumeBytes(TransferItem item, out long resumeBytes, out long sourceLength)
        {
            resumeBytes = 0;
            sourceLength = 0;

            try
            {
                if (!File.Exists(item.SourcePath) || string.IsNullOrWhiteSpace(item.DestinationRoot))
                    return false;

                var source = new FileInfo(item.SourcePath);
                sourceLength = source.Length;

                var root = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
                var finalPath = Path.Combine(root, item.RelativePath);
                var partPath = finalPath + ".camptransfer.part";
                var metaPath = partPath + ".json";

                if (!File.Exists(partPath) || !File.Exists(metaPath))
                    return false;

                using var metadata = JsonDocument.Parse(File.ReadAllText(metaPath));
                var rootElement = metadata.RootElement;
                if (!rootElement.TryGetProperty("SourceLength", out var lengthElement) ||
                    !rootElement.TryGetProperty("SourceLastWriteUtcTicks", out var ticksElement))
                    return false;

                if (lengthElement.GetInt64() != source.Length ||
                    ticksElement.GetInt64() != source.LastWriteTimeUtc.Ticks)
                    return false;

                var partLength = new FileInfo(partPath).Length;
                if (partLength <= 0 || partLength > source.Length)
                    return false;

                resumeBytes = partLength;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match)
                    yield return match;
                foreach (var nested in FindControls<T>(child))
                    yield return nested;
            }
        }

        private sealed record ResumeCandidate(TransferItem Item, long ResumeBytes, long SourceLength, bool CleanupOnly);
    }

    private sealed class CloseMessageFilter : IMessageFilter
    {
        private const int WmSysCommand = 0x0112;
        private const int ScClose = 0xF060;

        private readonly MainForm _form;
        private readonly Action _hideToTray;

        public CloseMessageFilter(MainForm form, Action hideToTray)
        {
            _form = form;
            _hideToTray = hideToTray;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (_form.IsDisposed ||
                m.HWnd != _form.Handle ||
                m.Msg != WmSysCommand ||
                (m.WParam.ToInt64() & 0xFFF0) != ScClose)
            {
                return false;
            }

            _hideToTray();
            return true;
        }
    }
}

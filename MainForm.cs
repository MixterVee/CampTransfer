using System.ComponentModel;

namespace CampTransfer;

public sealed class MainForm : Form
{
    private readonly BindingList<TransferItem> _queue;
    private readonly BindingSource _bindingSource = new();
    private readonly TransferEngine _engine = new();
    private readonly AppSettings _settings;
    private readonly DataGridView _grid = new();
    private readonly ComboBox _destinationBox = new();
    private readonly ComboBox _speedBox = new();
    private readonly Button _startButton = new() { Text = "Start", AutoSize = true };
    private readonly Button _pauseButton = new() { Text = "Pause", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Cancel Current", AutoSize = true, Enabled = false };
    private readonly ToolStripStatusLabel _statusLabel = new("Ready");
    private readonly ToolStripStatusLabel _queueSummaryLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly NotifyIcon _notifyIcon = new();
    private readonly System.Windows.Forms.Timer _notificationHideTimer = new() { Interval = 8000 };
    private CancellationTokenSource? _queueCts;
    private bool _closing;
    private double _speedLimitBytesPerSecond;

    public MainForm()
    {
        Text = "CampTransfer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 520);
        Size = new Size(1120, 650);
        Font = new Font("Segoe UI", 9F);
        AllowDrop = true;

        _notifyIcon.Icon = SystemIcons.Application;
        _notifyIcon.Text = "CampTransfer";
        _notificationHideTimer.Tick += (_, _) =>
        {
            _notificationHideTimer.Stop();
            _notifyIcon.Visible = false;
        };

        _settings = AppSettings.Load();
        _queue = new BindingList<TransferItem>(AppSettings.LoadQueue());
        _bindingSource.DataSource = _queue;

        BuildUi();
        WireEvents();
        RestoreSettings();
        UpdateSpeedLimitFromText();
        _engine.GetSpeedLimitBytesPerSecond = () => _speedLimitBytesPerSecond;
        UpdateStatus();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var destinationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 5,
            Margin = new Padding(0, 0, 0, 8)
        };
        destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        destinationPanel.Controls.Add(new Label
        {
            Text = "Destination:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 0)
        }, 0, 0);

        _destinationBox.Dock = DockStyle.Fill;
        _destinationBox.DropDownStyle = ComboBoxStyle.DropDown;
        destinationPanel.Controls.Add(_destinationBox, 1, 0);

        var browseButton = new Button { Text = "Browse...", AutoSize = true };
        var saveNamedButton = new Button { Text = "Save Name...", AutoSize = true };
        var applyDestinationButton = new Button { Text = "Set on Selected", AutoSize = true };
        destinationPanel.Controls.Add(browseButton, 2, 0);
        destinationPanel.Controls.Add(saveNamedButton, 3, 0);
        destinationPanel.Controls.Add(applyDestinationButton, 4, 0);
        root.Controls.Add(destinationPanel, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        var addFilesButton = new Button { Text = "Add Files", AutoSize = true };
        var addFolderButton = new Button { Text = "Add Folder", AutoSize = true };
        var removeButton = new Button { Text = "Remove", AutoSize = true };
        var upButton = new Button { Text = "Move Up", AutoSize = true };
        var downButton = new Button { Text = "Move Down", AutoSize = true };

        toolbar.Controls.Add(addFilesButton);
        toolbar.Controls.Add(addFolderButton);
        toolbar.Controls.Add(removeButton);
        toolbar.Controls.Add(upButton);
        toolbar.Controls.Add(downButton);
        toolbar.Controls.Add(new Label { Text = "   Upload limit:", AutoSize = true, Margin = new Padding(10, 8, 4, 0) });

        _speedBox.Width = 165;
        _speedBox.DropDownStyle = ComboBoxStyle.DropDown;
        _speedBox.Items.AddRange([
            "0.25 Mbps", "0.5 Mbps", "1 Mbps", "2 Mbps", "5 Mbps", "10 Mbps",
            "250 KB/s", "500 KB/s", "1 MB/s", "2 MB/s", "Unlimited"
        ]);
        toolbar.Controls.Add(_speedBox);
        toolbar.Controls.Add(_startButton);
        toolbar.Controls.Add(_pauseButton);
        toolbar.Controls.Add(_cancelButton);
        root.Controls.Add(toolbar, 0, 1);

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.DataSource = _bindingSource;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = nameof(TransferItem.FileName), Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Destination", DataPropertyName = nameof(TransferItem.DestinationDisplay), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 250 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Size", DataPropertyName = nameof(TransferItem.SizeText), Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Progress", DataPropertyName = nameof(TransferItem.ProgressText), Width = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Speed", DataPropertyName = nameof(TransferItem.Speed), Width = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ETA", DataPropertyName = nameof(TransferItem.Eta), Width = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(TransferItem.Status), Width = 165 });
        root.Controls.Add(_grid, 0, 2);

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(_queueSummaryLabel);
        root.Controls.Add(statusStrip, 0, 3);

        addFilesButton.Click += (_, _) => AddFiles();
        addFolderButton.Click += (_, _) => AddFolder();
        removeButton.Click += (_, _) => RemoveSelected();
        upButton.Click += (_, _) => MoveSelected(-1);
        downButton.Click += (_, _) => MoveSelected(1);
        browseButton.Click += (_, _) => BrowseDestination();
        saveNamedButton.Click += (_, _) => SaveNamedDestination();
        applyDestinationButton.Click += (_, _) => ApplyDestinationToSelected();
    }

    private void WireEvents()
    {
        _startButton.Click += async (_, _) => await StartQueueAsync();
        _pauseButton.Click += (_, _) => TogglePause();
        _cancelButton.Click += (_, _) => _engine.CancelCurrent();
        _speedBox.TextChanged += (_, _) =>
        {
            UpdateSpeedLimitFromText();
            SaveSettings();
            UpdateStatus();
        };
        _destinationBox.TextChanged += (_, _) => SaveSettings();
        _destinationBox.SelectionChangeCommitted += (_, _) =>
        {
            if (_destinationBox.SelectedItem is NamedDestination saved)
            {
                _destinationBox.Text = saved.Path;
                _destinationBox.SelectionStart = _destinationBox.Text.Length;
                RememberDestination(saved.Path);
            }
        };
        FormClosing += OnFormClosing;

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddPaths(paths);
        };

        _queue.ListChanged += (_, _) =>
        {
            SaveQueue();
            UpdateStatus();
        };
    }

    private void RestoreSettings()
    {
        RebuildDestinationItems();
        _destinationBox.Text = _settings.LastDestination;
        _speedBox.Text = string.IsNullOrWhiteSpace(_settings.SpeedLimitText) ? "2 Mbps" : _settings.SpeedLimitText;
    }

    private void RebuildDestinationItems()
    {
        var currentText = _destinationBox.Text;
        _destinationBox.BeginUpdate();
        try
        {
            _destinationBox.Items.Clear();
            foreach (var saved in _settings.NamedDestinations
                         .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Path))
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                _destinationBox.Items.Add(saved);
            }

            foreach (var recent in _settings.RecentDestinations
                         .Where(d => !string.IsNullOrWhiteSpace(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_settings.NamedDestinations.Any(n => n.Path.Equals(recent, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _destinationBox.Items.Add(recent);
            }
        }
        finally
        {
            _destinationBox.EndUpdate();
        }
        _destinationBox.Text = currentText;
    }

    private void SaveNamedDestination()
    {
        var path = _destinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Enter or browse to a destination first.", "Named Destination", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var suggestedName = _settings.NamedDestinations
            .FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

        if (!DestinationNameDialog.TryGetName(this, path, suggestedName, out var name))
            return;

        var existing = _settings.NamedDestinations
            .FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _settings.NamedDestinations.Add(new NamedDestination { Name = name, Path = path });
        }
        else
        {
            existing.Name = name;
            existing.Path = path;
        }

        RememberDestination(path);
        RebuildDestinationItems();
        _destinationBox.Text = path;
        SaveSettings();
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose files to queue",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(dialog.FileNames);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to queue",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var rootName = Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var file in Directory.EnumerateFiles(dialog.SelectedPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.Combine(rootName, Path.GetRelativePath(dialog.SelectedPath, file));
            AddFile(file, relative);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    AddFile(path, Path.GetFileName(path));
                }
                else if (Directory.Exists(path))
                {
                    var rootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.Combine(rootName, Path.GetRelativePath(path, file));
                        AddFile(file, relative);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not add item", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void AddFile(string path, string relativePath)
    {
        var info = new FileInfo(path);
        _queue.Add(new TransferItem
        {
            SourcePath = info.FullName,
            DestinationRoot = _destinationBox.Text.Trim(),
            RelativePath = relativePath,
            SizeBytes = info.Length,
            Status = string.IsNullOrWhiteSpace(_destinationBox.Text) ? "Destination needed" : "Queued"
        });
        RememberDestination(_destinationBox.Text);
    }

    private void BrowseDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the destination folder or network share",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_destinationBox.Text) ? _destinationBox.Text : ""
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destinationBox.Text = dialog.SelectedPath;
            RememberDestination(dialog.SelectedPath);
        }
    }

    private void ApplyDestinationToSelected()
    {
        var destination = _destinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Enter or browse to a destination first.", "Destination", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            if (row.DataBoundItem is not TransferItem item || item.Status is "Transferring" or "Paused" or "Resuming") continue;
            item.DestinationRoot = destination;
            item.Completed = false;
            item.Status = "Queued";
            item.ProgressPercent = 0;
            item.NotifyDestinationChanged();
        }
        _grid.Refresh();
        RememberDestination(destination);
        SaveQueue();
    }

    private void RemoveSelected()
    {
        var selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as TransferItem)
            .Where(i => i is not null)
            .Cast<TransferItem>()
            .ToList();

        foreach (var item in selected)
        {
            if (item.Status is "Transferring" or "Paused" or "Resuming" || item.Status.StartsWith("Retrying", StringComparison.Ordinal)) continue;
            _queue.Remove(item);
        }
    }

    private void MoveSelected(int direction)
    {
        if (_engine.IsRunning || _grid.SelectedRows.Count != 1) return;
        if (_grid.SelectedRows[0].DataBoundItem is not TransferItem item) return;

        var oldIndex = _queue.IndexOf(item);
        var newIndex = oldIndex + direction;
        if (newIndex < 0 || newIndex >= _queue.Count) return;

        _queue.RaiseListChangedEvents = false;
        _queue.RemoveAt(oldIndex);
        _queue.Insert(newIndex, item);
        _queue.RaiseListChangedEvents = true;
        _queue.ResetBindings();
        SaveQueue();

        _grid.ClearSelection();
        if (newIndex < _grid.Rows.Count)
            _grid.Rows[newIndex].Selected = true;
    }

    private async Task StartQueueAsync()
    {
        if (_engine.IsRunning) return;
        if (_queue.Count == 0)
        {
            MessageBox.Show(this, "Add at least one file first.", "CampTransfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!SpeedParser.TryParse(_speedBox.Text, out _))
        {
            MessageBox.Show(this, "Enter a speed such as 2 Mbps, 500 KB/s, 1 MB/s, or Unlimited.", "Upload limit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _speedBox.Focus();
            return;
        }

        foreach (var item in _queue.Where(i =>
                     i.Status is "Cancelled" or "Source missing" or "Destination needed" ||
                     i.Status.StartsWith("Error:", StringComparison.Ordinal)))
        {
            item.ResetRuntimeState();
        }

        _queueCts?.Dispose();
        _queueCts = new CancellationTokenSource();
        SetRunningUi(true);

        var snapshot = _queue.ToList();
        var workItems = snapshot.Where(i => i.Status != "Completed").ToList();
        var queueStopped = false;

        try
        {
            await _engine.RunQueueAsync(snapshot, OnItemChanged, _queueCts.Token);
        }
        catch (OperationCanceledException)
        {
            queueStopped = true;
            _statusLabel.Text = "Queue stopped";
        }
        finally
        {
            SetRunningUi(false);
            SaveQueue();
            UpdateStatus();
        }

        if (!queueStopped && workItems.Count > 0 && workItems.All(i => i.Status == "Completed"))
            ShowCompletionNotification(workItems.Count);
    }

    private void TogglePause()
    {
        if (!_engine.IsRunning) return;

        if (_engine.IsPaused)
        {
            _engine.Resume();
            _pauseButton.Text = "Pause";
            var active = _queue.FirstOrDefault(i => i.Status == "Paused");
            if (active is not null) active.Status = "Transferring";
            _statusLabel.Text = "Transferring";
        }
        else
        {
            _engine.Pause();
            _pauseButton.Text = "Resume";
            var active = _queue.FirstOrDefault(i => i.Status is "Transferring" or "Resuming");
            if (active is not null) active.Status = "Paused";
            _statusLabel.Text = "Paused";
        }
        UpdateStatus();
    }

    private void OnItemChanged(TransferItem item)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnItemChanged(item)));
            return;
        }

        _bindingSource.ResetCurrentItem();
        UpdateStatus();
        if (item.Status is "Completed" or "Cancelled" || item.Status.StartsWith("Error:", StringComparison.Ordinal))
            SaveQueue();
    }

    private void UpdateSpeedLimitFromText()
    {
        if (SpeedParser.TryParse(_speedBox.Text, out var speed))
            _speedLimitBytesPerSecond = speed;
    }

    private void SetRunningUi(bool running)
    {
        _startButton.Enabled = !running;
        _pauseButton.Enabled = running;
        _cancelButton.Enabled = running;
        _pauseButton.Text = "Pause";
        if (!running) _engine.Resume();
    }

    private void UpdateStatus()
    {
        var completed = _queue.Count(i => i.Status == "Completed");
        var active = _queue.FirstOrDefault(i =>
            i.Status is "Transferring" or "Paused" or "Resuming" ||
            i.Status.StartsWith("Retrying", StringComparison.Ordinal));

        if (active is not null)
        {
            _statusLabel.Text = active.Status.StartsWith("Retrying", StringComparison.Ordinal)
                ? $"{active.FileName} — {active.Status}"
                : $"{active.FileName} — {active.ProgressText} {active.Speed}".Trim();
        }
        else
        {
            _statusLabel.Text = _queue.Count == 0 ? "Ready" : $"{completed} of {_queue.Count} completed";
        }

        UpdateQueueSummary(active);
    }

    private void UpdateQueueSummary(TransferItem? active)
    {
        var remainingItems = _queue.Where(i => !i.Completed).ToList();
        if (remainingItems.Count == 0)
        {
            _queueSummaryLabel.Text = _queue.Count == 0 ? "" : "Queue complete";
            return;
        }

        var remainingBytes = remainingItems.Sum(i =>
        {
            var fractionRemaining = 1.0 - Math.Clamp(i.ProgressPercent / 100.0, 0, 1);
            return i.SizeBytes * fractionRemaining;
        });

        var fileWord = remainingItems.Count == 1 ? "file" : "files";
        var summary = $"{remainingItems.Count} {fileWord} left • {TransferItem.FormatBytes(remainingBytes)}";

        double estimateRate = 0;
        if (_engine.IsRunning && !_engine.IsPaused &&
            active?.Status.StartsWith("Retrying", StringComparison.Ordinal) != true)
        {
            estimateRate = active?.CurrentBytesPerSecond > 1
                ? active.CurrentBytesPerSecond
                : _speedLimitBytesPerSecond;
        }

        if (estimateRate > 1 && remainingBytes > 0)
        {
            var eta = TimeSpan.FromSeconds(remainingBytes / estimateRate);
            summary += $" • ~{FormatQueueEta(eta)}";
        }

        _queueSummaryLabel.Text = summary;
    }

    private static string FormatQueueEta(TimeSpan time)
    {
        if (time.TotalDays >= 1) return $"{(int)time.TotalDays}d {time.Hours}h";
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes}m";
        if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds}s";
        return $"{Math.Max(0, time.Seconds)}s";
    }

    private void ShowCompletionNotification(int transferredCount)
    {
        if (_closing) return;

        _notifyIcon.BalloonTipTitle = "CampTransfer";
        _notifyIcon.BalloonTipText = transferredCount == 1
            ? "Transfer completed."
            : $"All {transferredCount} transfers completed.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.Visible = true;
        _notifyIcon.ShowBalloonTip(5000);
        _notificationHideTimer.Stop();
        _notificationHideTimer.Start();
    }

    private void RememberDestination(string? destination)
    {
        destination = destination?.Trim();
        if (string.IsNullOrWhiteSpace(destination)) return;

        _settings.RecentDestinations.RemoveAll(d => d.Equals(destination, StringComparison.OrdinalIgnoreCase));
        _settings.RecentDestinations.Insert(0, destination);
        if (_settings.RecentDestinations.Count > 10)
            _settings.RecentDestinations.RemoveRange(10, _settings.RecentDestinations.Count - 10);

        var alreadyShown = _destinationBox.Items.Cast<object>().Any(i =>
            i is string text && text.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            i is NamedDestination saved && saved.Path.Equals(destination, StringComparison.OrdinalIgnoreCase));
        if (!alreadyShown)
            _destinationBox.Items.Add(destination);
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_closing) return;
        _settings.LastDestination = _destinationBox.Text.Trim();
        _settings.SpeedLimitText = _speedBox.Text.Trim();
        try { _settings.Save(); } catch { }
    }

    private void SaveQueue()
    {
        if (_closing) return;
        try { AppSettings.SaveQueue(_queue); } catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _queueCts?.Cancel();
        _notificationHideTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        try
        {
            _settings.LastDestination = _destinationBox.Text.Trim();
            _settings.SpeedLimitText = _speedBox.Text.Trim();
            _settings.Save();
            AppSettings.SaveQueue(_queue);
        }
        catch { }
    }
}

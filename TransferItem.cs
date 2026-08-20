using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CampTransfer;

public sealed class TransferItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private double _progressPercent;
    private string _speed = "";
    private string _eta = "";
    private double _currentBytesPerSecond;
    private string _operation = "Copy";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourcePath { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Completed { get; set; }

    public string Operation
    {
        get => _operation;
        set
        {
            var normalized = string.Equals(value, "Move", StringComparison.OrdinalIgnoreCase) ? "Move" : "Copy";
            if (_operation == normalized) return;
            _operation = normalized;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string FileName => Path.GetFileName(SourcePath);

    [JsonIgnore]
    public string SizeText => FormatBytes(SizeBytes);

    [JsonIgnore]
    public string DestinationDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DestinationRoot)) return "(not set)";
            var root = PathHelpers.NormalizeDestinationPath(DestinationRoot);
            return Path.Combine(root, RelativePath);
        }
    }

    [JsonIgnore]
    public string ProgressText => $"{ProgressPercent:0.0}%";

    [JsonIgnore]
    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.01) return;
            _progressPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string Speed
    {
        get => _speed;
        set
        {
            if (_speed == value) return;
            _speed = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string Eta
    {
        get => _eta;
        set
        {
            if (_eta == value) return;
            _eta = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public double CurrentBytesPerSecond
    {
        get => _currentBytesPerSecond;
        set
        {
            if (Math.Abs(_currentBytesPerSecond - value) < 0.5) return;
            _currentBytesPerSecond = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyDestinationChanged() => OnPropertyChanged(nameof(DestinationDisplay));

    public void ResetRuntimeState()
    {
        Operation = Operation;
        Status = Completed ? "Completed" :
            !File.Exists(SourcePath) ? "Source missing" :
            string.IsNullOrWhiteSpace(DestinationRoot) ? "Destination needed" : "Queued";
        ProgressPercent = Completed ? 100 : 0;
        Speed = "";
        Eta = "";
        CurrentBytesPerSecond = 0;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}

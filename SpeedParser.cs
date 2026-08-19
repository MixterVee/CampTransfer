using System.Globalization;
using System.Text.RegularExpressions;

namespace CampTransfer;

public static partial class SpeedParser
{
    [GeneratedRegex(@"^\s*([0-9]+(?:[\.,][0-9]+)?)\s*(kbps|mbps|gbps|b/s|kb/s|mb/s|gb/s|kib/s|mib/s|gib/s)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();

    public static bool TryParse(string? text, out double bytesPerSecond)
    {
        bytesPerSecond = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (text.Equals("Unlimited", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("No limit", StringComparison.OrdinalIgnoreCase))
        {
            bytesPerSecond = 0;
            return true;
        }

        var match = SpeedRegex().Match(text);
        if (!match.Success) return false;

        var numberText = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;

        var unit = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "mbps";
        bytesPerSecond = unit switch
        {
            "kbps" => value * 1_000 / 8,
            "mbps" => value * 1_000_000 / 8,
            "gbps" => value * 1_000_000_000 / 8,
            "b/s" => value,
            "kb/s" => value * 1_000,
            "mb/s" => value * 1_000_000,
            "gb/s" => value * 1_000_000_000,
            "kib/s" => value * 1024,
            "mib/s" => value * 1024 * 1024,
            "gib/s" => value * 1024 * 1024 * 1024,
            _ => 0
        };
        return bytesPerSecond >= 0;
    }
}

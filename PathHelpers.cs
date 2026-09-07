using System.Runtime.InteropServices;
using System.Text;

namespace CampTransfer;

internal static class PathHelpers
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    public static string NormalizeDestinationPath(string? path)
    {
        path = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return path;

        // A named destination is displayed in the editable ComboBox as
        // "Name — Path". WinForms can briefly expose that display text through
        // ComboBox.Text before SelectionChangeCommitted replaces it with the real
        // path. Never allow the friendly display string to become a queue path.
        path = ExtractNamedDestinationPath(path);

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }

        if (!OperatingSystem.IsWindows() || path.Length < 2 || path[1] != ':')
            return path;

        var drive = path[..2];
        var buffer = new StringBuilder(1024);
        if (QueryDosDevice(drive, buffer, buffer.Capacity) == 0)
            return path;

        var target = buffer.ToString();
        const string dosPrefix = @"\??\";
        if (!target.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase))
            return path;

        var localTarget = target[dosPrefix.Length..];

        // True network mappings are intentionally left alone. We only collapse
        // SUBST-style mappings such as Z: -> C:\SomeFolder back to the local path.
        if (localTarget.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase) ||
            localTarget.Length < 3 ||
            localTarget[1] != ':' ||
            (localTarget[2] != '\\' && localTarget[2] != '/'))
        {
            return path;
        }

        var suffix = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
        try
        {
            return Path.GetFullPath(string.IsNullOrEmpty(suffix)
                ? localTarget
                : Path.Combine(localTarget, suffix));
        }
        catch
        {
            return path;
        }
    }

    private static string ExtractNamedDestinationPath(string value)
    {
        const string separator = " — ";
        var separatorIndex = value.LastIndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex < 0) return value;

        var candidate = value[(separatorIndex + separator.Length)..].Trim();
        if (LooksLikeRootedWindowsPath(candidate))
            return candidate;

        return value;
    }

    private static bool LooksLikeRootedWindowsPath(string value)
    {
        if (value.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        return value.Length >= 3 &&
               char.IsLetter(value[0]) &&
               value[1] == ':' &&
               (value[2] == '\\' || value[2] == '/');
    }

    public static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

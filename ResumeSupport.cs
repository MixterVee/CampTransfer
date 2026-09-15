using System.Text.Json;

namespace CampTransfer;

internal static class ResumeSupport
{
    public static bool TryGetResumeBytes(TransferItem item, out long resumeBytes, out long sourceLength)
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

    public static bool MarkResumeAvailable(TransferItem item)
    {
        if (!TryGetResumeBytes(item, out var resumeBytes, out var sourceLength))
            return false;

        item.Status = "Resume available";
        item.ProgressPercent = sourceLength <= 0 ? 100 : (double)resumeBytes / sourceLength * 100;
        return true;
    }
}

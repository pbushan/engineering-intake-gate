using System.IO.Compression;

namespace IntakeGate.Infrastructure.Evidence;

internal static class OpenXmlPackageSafety
{
    private const int MaximumEntries = 10_000;
    private const long MaximumExpandedBytes = 200L * 1024 * 1024;
    private const int MaximumExpansionRatio = 50;

    public static void Validate(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Open XML package has too many entries.");

        var relativeExpansionLimit = content.LongLength > long.MaxValue / MaximumExpansionRatio
            ? long.MaxValue
            : content.LongLength * MaximumExpansionRatio;
        var expandedLimit = Math.Min(MaximumExpandedBytes, Math.Max(content.LongLength, relativeExpansionLimit));
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."))
                throw new InvalidDataException("Open XML package contains an unsafe entry path.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > expandedLimit)
                throw new InvalidDataException("Open XML package expansion limit exceeded.");
        }
    }
}

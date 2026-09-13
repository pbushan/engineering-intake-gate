using System.Diagnostics;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

/// <summary>
/// Deterministically orders attachment work, applies input budgets before parsing, and dispatches
/// only bounded in-memory buffers to format processors. It performs no network operations.
/// </summary>
public sealed class AttachmentProcessingService(IEnumerable<IAttachmentProcessor> processors) : IAttachmentProcessingService
{
    private readonly IAttachmentProcessor[] processors = processors.ToArray();

    public async ValueTask<IReadOnlyList<AttachmentProcessingResult>> ProcessAsync(
        IReadOnlyList<RawAttachmentMetadata> attachments,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        Validate(limits, maximumExtractedCharacters);

        var ordered = attachments
            .OrderBy(attachment => attachment.Reference, StringComparer.Ordinal)
            .ThenBy(attachment => attachment.FileName, StringComparer.Ordinal)
            .ToArray();
        var results = new List<AttachmentProcessingResult>(ordered.Length);
        long aggregateBytes = 0;
        var imageCount = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attachment = ordered[index];
            var stopwatch = Stopwatch.StartNew();
            var contentSource = attachment.Content ?? (attachment.ContentBytes is null ? null : new BufferedAttachmentContent(attachment.ContentBytes));

            if (index >= limits.MaximumCount)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Partial, "AttachmentCountLimitExceeded", stopwatch));
                continue;
            }

            var declaredLength = contentSource?.Length ?? attachment.SizeBytes;
            if (declaredLength is > 0 && declaredLength > limits.MaximumBytesPerAttachment)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Partial, "PerAttachmentByteLimitExceeded", stopwatch));
                continue;
            }

            if (declaredLength is > 0 && declaredLength > limits.MaximumAggregateBytes - aggregateBytes)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Partial, "AggregateAttachmentByteLimitExceeded", stopwatch));
                continue;
            }

            if (contentSource is null)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Unavailable, "AttachmentContentNotProvided", stopwatch));
                continue;
            }

            byte[] bytes;
            try
            {
                await using var source = await contentSource.OpenReadAsync(cancellationToken);
                bytes = await ReadBoundedAsync(
                    source,
                    Math.Min(limits.MaximumBytesPerAttachment, limits.MaximumAggregateBytes - aggregateBytes),
                    cancellationToken);
            }
            catch (AttachmentLimitExceededException exception)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Partial, exception.Category, stopwatch));
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AttachmentContentUnavailableException exception)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Unavailable, exception.SafeCategory, stopwatch));
                continue;
            }
            catch (Exception)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Unavailable, "AttachmentContentUnavailable", stopwatch));
                continue;
            }

            aggregateBytes += bytes.LongLength;
            var mediaType = AttachmentMediaDetector.Detect(attachment.ContentType, attachment.FileName, bytes);
            var detected = new DetectedAttachment(attachment.Reference, attachment.FileName, mediaType, bytes);

            if (mediaType is "image/png" or "image/jpeg")
            {
                imageCount++;
                if (imageCount > limits.MaximumImageCount)
                {
                    results.Add(Result(attachment, AttachmentProcessingStatus.Partial, "ImageCountLimitExceeded", stopwatch, mediaType, bytes.LongLength));
                    continue;
                }
                if (bytes.LongLength > Math.Min(limits.MaximumImageBytes, limits.MaximumBytesPerAttachment))
                {
                    results.Add(Result(attachment, AttachmentProcessingStatus.Partial, "ImageByteLimitExceeded", stopwatch, mediaType, bytes.LongLength));
                    continue;
                }
            }

            var processor = processors.FirstOrDefault(candidate => candidate.CanProcess(detected));
            if (processor is null)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Unsupported, "UnsupportedAttachmentType", stopwatch, mediaType, bytes.LongLength));
                continue;
            }

            try
            {
                var output = await processor.ProcessAsync(detected, limits, maximumExtractedCharacters, cancellationToken);
                stopwatch.Stop();
                results.Add(new AttachmentProcessingResult(
                    attachment.Reference, attachment.FileName, mediaType, bytes.LongLength,
                    output.ProcessingStatus, output.InspectionMode, output.ExtractedEvidence,
                    output.Truncated, output.Sampled, output.PagesAvailable, output.PagesInspected,
                    output.FailureCategory, output.VisualContent, stopwatch.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                results.Add(Result(attachment, AttachmentProcessingStatus.Error, "AttachmentProcessingFailed", stopwatch, mediaType, bytes.LongLength));
            }
        }

        return results;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new AttachmentLimitExceededException("AggregateAttachmentByteLimitExceeded");
        using var destination = new MemoryStream((int)Math.Min(maximumBytes, 81_920));
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var remaining = maximumBytes - total;
            var probeLength = remaining >= buffer.Length ? buffer.Length : checked((int)remaining + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, probeLength), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new AttachmentLimitExceededException("AttachmentInputBudgetExceeded");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static AttachmentProcessingResult Result(
        RawAttachmentMetadata attachment,
        AttachmentProcessingStatus status,
        string failure,
        Stopwatch stopwatch,
        string? mediaType = null,
        long? size = null)
    {
        stopwatch.Stop();
        return new AttachmentProcessingResult(
            attachment.Reference, attachment.FileName, mediaType ?? attachment.ContentType,
            size ?? attachment.Content?.Length ?? attachment.ContentBytes?.LongLength ?? attachment.SizeBytes, status, AttachmentInspectionMode.None,
            string.Empty, status == AttachmentProcessingStatus.Partial, false, null, null, failure, null,
            stopwatch.ElapsedMilliseconds);
    }

    private static void Validate(AttachmentLimits limits, int maximumExtractedCharacters)
    {
        if (limits.MaximumCount <= 0 || limits.MaximumBytesPerAttachment <= 0 || limits.MaximumAggregateBytes <= 0 ||
            limits.MaximumPdfPages <= 0 || limits.MaximumImageCount <= 0 || limits.MaximumImageBytes <= 0 ||
            limits.MaximumCsvRows <= 0 || limits.MaximumStructuredTextDepth <= 0 || maximumExtractedCharacters <= 0 ||
            limits.MaximumBytesPerAttachment > int.MaxValue || limits.MaximumImageBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(limits), "Attachment processing limits must be positive.");
    }

    private sealed class AttachmentLimitExceededException(string category) : Exception
    {
        public string Category { get; } = category;
    }
}

internal static class AttachmentMediaDetector
{
    public static string Detect(string? declaredMediaType, string fileName, ReadOnlySpan<byte> content)
    {
        var declared = declaredMediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (content.StartsWith("%PDF-"u8)) return "application/pdf";
        if (content.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff) return "image/jpeg";

        if (!LooksLikeText(content)) return "application/octet-stream";
        var leading = DecodePrefix(content).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (declared is "application/json" or "text/json" || leading.StartsWith('{') || leading.StartsWith('[')) return "application/json";
        if (declared is "application/xml" or "text/xml" || leading.StartsWith('<')) return "application/xml";
        if (declared == "text/csv") return "text/csv";
        if (declared is not null && declared.StartsWith("text/", StringComparison.Ordinal))
            return declared is "text/plain" or "text/x-log" ? declared : "text/plain";

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".log" => "text/x-log",
            ".txt" => "text/plain",
            _ => "text/plain"
        };
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0) return true;
        var sample = content[..Math.Min(content.Length, 4096)];
        if (sample.IndexOf((byte)0) >= 0) return false;
        var control = 0;
        foreach (var value in sample)
            if (value < 0x09 || value is > 0x0d and < 0x20) control++;
        return control * 20 <= sample.Length;
    }

    private static string DecodePrefix(ReadOnlySpan<byte> content)
    {
        try { return System.Text.Encoding.UTF8.GetString(content[..Math.Min(content.Length, 4096)]); }
        catch { return string.Empty; }
    }
}

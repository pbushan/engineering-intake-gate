using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class ImageAttachmentProcessor : IAttachmentProcessor
{
    public bool CanProcess(DetectedAttachment attachment) => attachment.MediaType is "image/png" or "image/jpeg";

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var format = Image.DetectFormat(attachment.Content);
            var info = Image.Identify(attachment.Content);
            var detectedMediaType = format.DefaultMimeType.ToLowerInvariant();
            if (info is null || detectedMediaType != attachment.MediaType || info.Width <= 0 || info.Height <= 0)
                return ValueTask.FromResult(Error());

            return ValueTask.FromResult(new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.Image,
                $"Image prepared for visual inspection ({info.Width}x{info.Height}); deterministic text redaction cannot inspect text embedded in pixels.",
                false,
                false,
                null,
                null,
                null,
                new VisualAttachmentContent(attachment.MediaType, attachment.Content)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(Error());
        }
    }

    private static AttachmentProcessorOutput Error() => new(
        AttachmentProcessingStatus.Error, AttachmentInspectionMode.None, string.Empty,
        false, false, null, null, "MalformedImage");
}

using System.Text;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using UglyToad.PdfPig;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class PdfAttachmentProcessor : IAttachmentProcessor
{
    public bool CanProcess(DetectedAttachment attachment) => attachment.MediaType == "application/pdf";

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = PdfDocument.Open(attachment.Content);
            var pagesAvailable = document.NumberOfPages;
            var pagesInspected = Math.Min(pagesAvailable, limits.MaximumPdfPages);
            var builder = new StringBuilder(Math.Min(maximumExtractedCharacters, 16_384));
            var characterLimitHit = false;
            var hasExtractableText = false;
            for (var pageNumber = 1; pageNumber <= pagesInspected; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageText = document.GetPage(pageNumber).Text;
                hasExtractableText |= !string.IsNullOrWhiteSpace(pageText);
                var prefix = $"[PDF page {pageNumber}]\n";
                if (builder.Length + prefix.Length + pageText.Length > maximumExtractedCharacters)
                {
                    var remaining = Math.Max(0, maximumExtractedCharacters - builder.Length);
                    var candidate = prefix + pageText;
                    builder.Append(candidate.AsSpan(0, Math.Min(candidate.Length, remaining)));
                    characterLimitHit = true;
                    break;
                }
                builder.Append(prefix).Append(pageText).Append('\n');
            }

            var pagePartial = pagesInspected < pagesAvailable;
            var noText = !hasExtractableText;
            var partial = pagePartial || characterLimitHit || noText;
            var category = noText ? "PdfNoExtractableText" : pagePartial ? "PdfPageLimitExceeded" : characterLimitHit ? "EvidenceCharacterLimitExceeded" : null;
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                partial ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.PdfText,
                builder.ToString().TrimEnd(),
                partial,
                pagePartial,
                pagesAvailable,
                pagesInspected,
                category));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Error, AttachmentInspectionMode.None, string.Empty,
                false, false, null, null, "MalformedPdf"));
        }
    }
}

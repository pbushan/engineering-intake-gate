using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class DocxAttachmentProcessor : IAttachmentProcessor, IAttachmentLimitsFingerprintProvider
{
    public string ProcessorVersion => "docx-v1";
    public bool CanProcess(DetectedAttachment attachment) =>
        attachment.MediaType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public string CreateLimitsFingerprint(AttachmentLimits limits, int maximumExtractedCharacters) =>
        AnalysisFingerprint.Sha256(maximumExtractedCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(DetectedAttachment attachment, AttachmentLimits limits,
        int maximumExtractedCharacters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            OpenXmlPackageSafety.Validate(attachment.Content);
            using var stream = new MemoryStream(attachment.Content, writable: false);
            using var document = WordprocessingDocument.Open(stream, false, new OpenSettings
            {
                AutoSave = false,
                MaxCharactersInPart = Math.Max(1, Math.Min(maximumExtractedCharacters * 20L, 20_000_000L))
            });
            var mainDocument = document.MainDocumentPart?.Document ?? throw new InvalidDataException();
            var body = mainDocument.Body ?? throw new InvalidDataException();
            var builder = new StringBuilder(Math.Min(maximumExtractedCharacters, 32_768));
            var truncated = false;
            foreach (var element in body.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = element switch
                {
                    Paragraph paragraph => paragraph.InnerText,
                    Table table => string.Join("\n", table.Elements<TableRow>().Select(row =>
                        string.Join(" | ", row.Elements<TableCell>().Select(cell => cell.InnerText)))),
                    _ => string.Empty
                };
                if (string.IsNullOrWhiteSpace(line)) continue;
                var remaining = maximumExtractedCharacters - builder.Length;
                if (remaining <= 0) { truncated = true; break; }
                var normalized = line.Replace('\r', ' ').Trim() + "\n";
                if (normalized.Length > remaining) truncated = true;
                builder.Append(normalized.AsSpan(0, Math.Min(normalized.Length, remaining)));
                if (truncated) break;
            }
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                truncated ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.StructuredText, builder.ToString().TrimEnd(), truncated, false,
                null, null, truncated ? "EvidenceCharacterLimitExceeded" : null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Error, AttachmentInspectionMode.None, string.Empty,
                false, false, null, null, "MalformedOrUnsafeDocx"));
        }
    }
}

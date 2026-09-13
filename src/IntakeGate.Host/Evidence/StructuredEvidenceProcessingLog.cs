using IntakeGate.Application.Evidence;

namespace IntakeGate.Host.Evidence;

public sealed class StructuredEvidenceProcessingLog(ILogger<StructuredEvidenceProcessingLog> logger)
    : IEvidenceProcessingLog
{
    public void ContentCollectionCompleted(ContentCollectionLogEntry entry)
    {
        logger.LogInformation(
            "Evidence workflow stage completed. Event={EventName} Stage={Stage} WorkItemId={WorkItemId} Revision={Revision} FieldCount={FieldCount} HumanCommentCount={HumanCommentCount} ExcludedValidatorCommentCount={ExcludedValidatorCommentCount} AttachmentMetadataCount={AttachmentMetadataCount} Truncated={Truncated}",
            "EvidenceStageCompleted",
            "CONTENT_COLLECTION",
            entry.WorkItemId,
            entry.Revision,
            entry.FieldCount,
            entry.HumanCommentCount,
            entry.ExcludedValidatorCommentCount,
            entry.AttachmentMetadataCount,
            entry.Truncated);
    }

    public void SecretRedactionCompleted(SecretRedactionLogEntry entry)
    {
        logger.LogInformation(
            "Evidence workflow stage completed. Event={EventName} Stage={Stage} WorkItemId={WorkItemId} Revision={Revision} RedactionOccurred={RedactionOccurred} RedactionCount={RedactionCount}",
            "EvidenceStageCompleted",
            "SECRET_REDACTION",
            entry.WorkItemId,
            entry.Revision,
            entry.RedactionOccurred,
            entry.RedactionCount);
    }

    public void AttachmentProcessingCompleted(AttachmentProcessingLogEntry entry)
    {
        logger.LogInformation(
            "Evidence workflow stage completed. Event={EventName} Stage={Stage} WorkItemId={WorkItemId} Revision={Revision} AttachmentId={AttachmentId} AttachmentName={AttachmentName} MediaType={MediaType} OriginalSize={OriginalSize} ProcessingStatus={ProcessingStatus} InspectionMode={InspectionMode} Truncated={Truncated} Sampled={Sampled} PagesAvailable={PagesAvailable} PagesInspected={PagesInspected} FailureCategory={FailureCategory} ProcessingDurationMilliseconds={ProcessingDurationMilliseconds}",
            "EvidenceStageCompleted",
            "ATTACHMENT_PROCESSING",
            entry.WorkItemId,
            entry.Revision,
            entry.AttachmentId,
            entry.Name,
            entry.MediaType,
            entry.OriginalSize,
            entry.ProcessingStatus,
            entry.InspectionMode,
            entry.Truncated,
            entry.Sampled,
            entry.PagesAvailable,
            entry.PagesInspected,
            entry.FailureCategory,
            entry.ProcessingDurationMilliseconds);
    }
}

using IntakeGate.Application.Configuration;

namespace IntakeGate.Application.Evidence;

public interface IEvidencePreprocessor
{
    EvaluationEvidence Prepare(RawWorkItem workItem, ProcessingConfiguration processing);

    ValueTask<EvaluationEvidence> PrepareAsync(
        RawWorkItem workItem,
        ProcessingConfiguration processing,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Prepare(workItem, processing));

    ValueTask<EvaluationEvidence> PrepareAsync(
        RawWorkItem workItem,
        ProcessingConfiguration processing,
        EvidencePreparationOptions options,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(workItem, processing, cancellationToken);
}

public sealed record EvidencePreparationOptions(
    string Organization,
    string Project,
    bool ForceFresh,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IContentNormalizer
{
    string Normalize(string? content, WorkItemContentFormat format);
}

public interface ISecretRedactor
{
    SecretRedactionResult Redact(string content);
}

public sealed record SecretRedactionResult(
    string Content,
    int RedactionCount,
    IReadOnlyDictionary<string, int> CategoryCounts);

/// <summary>Receives only bounded, non-content metadata suitable for structured logging.</summary>
public interface IEvidenceProcessingLog
{
    void ContentCollectionCompleted(ContentCollectionLogEntry entry);
    void SecretRedactionCompleted(SecretRedactionLogEntry entry);
    void AttachmentProcessingCompleted(AttachmentProcessingLogEntry entry) { }
}

public sealed record ContentCollectionLogEntry(
    string WorkItemId,
    string Revision,
    int FieldCount,
    int HumanCommentCount,
    int ExcludedValidatorCommentCount,
    int AttachmentMetadataCount,
    bool Truncated);

public sealed record SecretRedactionLogEntry(
    string WorkItemId,
    string Revision,
    bool RedactionOccurred,
    int RedactionCount);

public sealed record AttachmentProcessingLogEntry(
    string WorkItemId,
    string Revision,
    string AttachmentId,
    string Name,
    string? MediaType,
    long? OriginalSize,
    AttachmentProcessingStatus ProcessingStatus,
    AttachmentInspectionMode InspectionMode,
    bool Truncated,
    bool Sampled,
    int? PagesAvailable,
    int? PagesInspected,
    string? FailureCategory,
    long ProcessingDurationMilliseconds);

public interface IAttachmentProcessingService
{
    ValueTask<IReadOnlyList<AttachmentProcessingResult>> ProcessAsync(
        IReadOnlyList<RawAttachmentMetadata> attachments,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken = default);
}

public interface ICacheAwareAttachmentProcessingService : IAttachmentProcessingService
{
    ValueTask<IReadOnlyList<AttachmentProcessingResult>> ProcessAsync(
        IReadOnlyList<RawAttachmentMetadata> attachments,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        EvidencePreparationOptions options,
        CancellationToken cancellationToken = default);
}

public interface IAttachmentProcessor
{
    string ProcessorIdentity => GetType().Name;
    string ProcessorVersion => "1";
    bool CanProcess(DetectedAttachment attachment);
    ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken);
}

public sealed record DetectedAttachment(
    string AttachmentId,
    string Name,
    string MediaType,
    byte[] Content);

public sealed record AttachmentProcessorOutput(
    AttachmentProcessingStatus ProcessingStatus,
    AttachmentInspectionMode InspectionMode,
    string ExtractedEvidence,
    bool Truncated,
    bool Sampled,
    int? PagesAvailable,
    int? PagesInspected,
    string? FailureCategory,
    [property: System.Text.Json.Serialization.JsonIgnore] VisualAttachmentContent? VisualContent = null);

public sealed record AttachmentProcessingResult(
    string AttachmentId,
    string Name,
    string? MediaType,
    long? OriginalSize,
    AttachmentProcessingStatus ProcessingStatus,
    AttachmentInspectionMode InspectionMode,
    string ExtractedEvidence,
    bool Truncated,
    bool Sampled,
    int? PagesAvailable,
    int? PagesInspected,
    string? FailureCategory,
    [property: System.Text.Json.Serialization.JsonIgnore] VisualAttachmentContent? VisualContent = null,
    long ProcessingDurationMilliseconds = 0)
{
    public string? ContentSha256 { get; init; }
    public string? ArtifactId { get; init; }
    public string? ProcessorIdentity { get; init; }
    public string? ProcessorVersion { get; init; }
    public bool CacheReused { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, int> RedactionCategoryCounts { get; init; } = new Dictionary<string, int>();
}

public sealed class VisualAttachmentContent
{
    private readonly byte[] content;

    public VisualAttachmentContent(string mediaType, ReadOnlySpan<byte> content)
    {
        MediaType = mediaType;
        this.content = content.ToArray();
    }

    public string MediaType { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlyMemory<byte> Content => content;
}

public enum AttachmentProcessingStatus
{
    Processed,
    Partial,
    Unsupported,
    Unavailable,
    Error
}

public enum AttachmentInspectionMode
{
    Text,
    StructuredText,
    PdfText,
    Image,
    None
}

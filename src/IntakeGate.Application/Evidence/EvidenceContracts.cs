using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;

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

public interface ICacheAwareAttachmentProcessor : IAttachmentProcessor
{
    ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        AttachmentProcessorCacheContext cacheContext,
        CancellationToken cancellationToken);
}

public interface IAttachmentLimitsFingerprintProvider
{
    string CreateLimitsFingerprint(AttachmentLimits limits, int maximumExtractedCharacters);
}

public sealed record AttachmentProcessorCacheContext(
    IAnalysisCacheRepository Cache,
    EvidencePreparationOptions Options,
    string ContentSha256,
    string ProcessingLimitsFingerprint);

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
    [property: System.Text.Json.Serialization.JsonIgnore] VisualAttachmentContent? VisualContent = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public PdfEvidenceMetadata? Pdf { get; init; }
    public AudioEvidenceMetadata? Audio { get; init; }
    public VideoEvidenceMetadata? Video { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SelectedScreenshotContent> SelectedScreenshots { get; init; } = [];
    public IReadOnlyList<AiProviderInteractionUsage> AiInteractions { get; init; } = [];
}

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
    public PdfEvidenceMetadata? Pdf { get; init; }
    public AudioEvidenceMetadata? Audio { get; init; }
    public VideoEvidenceMetadata? Video { get; init; }
    public IReadOnlyList<SelectedKeyScreenshot> SelectedKeyScreenshots { get; init; } = [];
    public IReadOnlyList<AiProviderInteractionUsage> AiInteractions { get; init; } = [];
}

public sealed class SelectedScreenshotContent
{
    private readonly byte[] content;

    public SelectedScreenshotContent(double timestampSeconds, int width, int height, string observation,
        string provenance, ReadOnlySpan<byte> content)
    {
        TimestampSeconds = timestampSeconds;
        Width = width;
        Height = height;
        Observation = observation;
        Provenance = provenance;
        this.content = content.ToArray();
    }

    public double TimestampSeconds { get; }
    public int Width { get; }
    public int Height { get; }
    public string Observation { get; }
    public string Provenance { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlyMemory<byte> Content => content;
}

public sealed record AudioTranscriptionRequest(
    string AudioPath,
    string SourceMediaType,
    int MaximumTranscriptCharacters,
    TimeSpan Timeout);

public sealed record AudioTranscriptionResult(
    EvidenceSubstageStatus Status,
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<string> Warnings,
    AiProviderInteractionUsage? Interaction = null);

public sealed record FrameVisionRequest(
    double TimestampSeconds,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    TimeSpan Timeout);

public sealed record FrameVisionResult(
    EvidenceSubstageStatus Status,
    string? Observation,
    IReadOnlyList<string> Warnings,
    AiProviderInteractionUsage? Interaction = null);

public interface IAttachmentEvidenceAiProvider
{
    string ProviderIdentity { get; }
    string TranscriptionModel { get; }
    string TranscriptionVersion { get; }
    string VisionModel { get; }
    string VisionVersion { get; }
    Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken);
    Task<FrameVisionResult> ObserveFrameAsync(FrameVisionRequest request, CancellationToken cancellationToken);
}

public sealed class UnavailableAttachmentEvidenceAiProvider : IAttachmentEvidenceAiProvider
{
    public string ProviderIdentity => "unavailable";
    public string TranscriptionModel => "unavailable";
    public string TranscriptionVersion => "transcription-v1";
    public string VisionModel => "unavailable";
    public string VisionVersion => "frame-observation-v1";
    public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AudioTranscriptionResult(EvidenceSubstageStatus.Unavailable, [], ["TranscriptionProviderUnavailable"]));
    public Task<FrameVisionResult> ObserveFrameAsync(FrameVisionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new FrameVisionResult(EvidenceSubstageStatus.Unavailable, null, ["VisionProviderUnavailable"]));
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
    PdfTextAndVision,
    Spreadsheet,
    Image,
    AudioTranscript,
    VideoComposite,
    None
}

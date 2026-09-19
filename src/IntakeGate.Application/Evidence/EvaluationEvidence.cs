namespace IntakeGate.Application.Evidence;

using System.Text.Json.Serialization;

/// <summary>
/// The normalized, bounded, and secret-redacted representation that may eventually cross the AI
/// trust boundary. RawWorkItem must never be used for that purpose.
/// </summary>
public sealed record EvaluationEvidence(
    string WorkItemId,
    string Revision,
    string WorkItemType,
    string Title,
    IReadOnlyList<EvaluationEvidenceField> Fields,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EvaluationEvidenceRelation> Relations,
    IReadOnlyList<EvaluationEvidenceComment> Comments,
    IReadOnlyList<EvaluationAttachmentMetadata> Attachments,
    EvidenceProcessingDisclosure Processing,
    RedactionMetadata Redaction)
{
    /// <summary>Transient visual payloads; never serialized, logged, or persisted.</summary>
    [JsonIgnore]
    public IReadOnlyList<VisualEvidence> VisualEvidence { get; init; } = [];
}

public sealed record EvaluationEvidenceField(
    string ReferenceName,
    string? DisplayName,
    string Value,
    string ValueKind,
    WorkItemContentFormat SourceFormat,
    bool Truncated);

public sealed record EvaluationEvidenceComment(
    string Id,
    string? AuthorDisplayName,
    DateTimeOffset? CreatedAt,
    string Content,
    bool Truncated);

public sealed record EvaluationEvidenceRelation(
    string Reference,
    string RelationType,
    string? DisplayName,
    bool Truncated);

public sealed record EvaluationAttachmentMetadata(
    string Reference,
    string FileName,
    string? ContentType,
    long? SizeBytes,
    bool Truncated,
    AttachmentProcessingStatus ProcessingStatus = AttachmentProcessingStatus.Unavailable,
    AttachmentInspectionMode InspectionMode = AttachmentInspectionMode.None,
    string ExtractedEvidence = "",
    bool Sampled = false,
    int? PagesAvailable = null,
    int? PagesInspected = null,
    string? FailureCategory = null,
    bool RequiresVisualInspection = false)
{
    public string? ContentSha256 { get; init; }
    public string? ArtifactId { get; init; }
    public string? ProcessorIdentity { get; init; }
    public string? ProcessorVersion { get; init; }
    public bool CacheReused { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class VisualEvidence
{
    private readonly byte[] content;

    public VisualEvidence(string attachmentId, string name, string mediaType, ReadOnlySpan<byte> content)
    {
        AttachmentId = attachmentId;
        Name = name;
        MediaType = mediaType;
        this.content = content.ToArray();
    }

    public string AttachmentId { get; }
    public string Name { get; }
    public string MediaType { get; }
    [JsonIgnore]
    public ReadOnlyMemory<byte> Content => content;
}

public sealed record EvidenceProcessingDisclosure(
    bool TruncationOccurred,
    bool AggregateTextLimitReached,
    bool WorkItemTypeTruncated,
    bool TitleTruncated,
    bool DescriptionTruncated,
    int TruncatedFieldCount,
    int OmittedFieldCount,
    int IncludedTagCount,
    int AvailableTagCount,
    int TruncatedTagCount,
    int OmittedTagCount,
    int IncludedRelationCount,
    int AvailableRelationCount,
    int TruncatedRelationCount,
    int OmittedRelationCount,
    int IncludedCommentCount,
    int AvailableHumanCommentCount,
    int ExcludedValidatorCommentCount,
    int TruncatedCommentCount,
    int OmittedCommentCount,
    int IncludedAttachmentMetadataCount,
    int AvailableAttachmentMetadataCount,
    int TruncatedAttachmentMetadataCount,
    int OmittedAttachmentMetadataCount,
    bool AttachmentMetadataAvailable,
    bool AttachmentContentInspected,
    int IncludedTextCharacters);

public sealed record RedactionMetadata(
    bool RedactionOccurred,
    int RedactionCount,
    IReadOnlyDictionary<string, int> CategoryCounts);

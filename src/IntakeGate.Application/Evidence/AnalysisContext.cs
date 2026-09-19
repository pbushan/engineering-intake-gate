using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Application.Evidence;

public static class AnalysisContextVersions
{
    public const string Schema = "analysis-context-v1";
    public const string NormalizedEvidence = "normalized-evidence-v1";
    public const string EvaluationContract = "intake-evaluation-v2";
}

public enum AnalysisExecutionMode
{
    NormalReuseEligible,
    ForceFresh
}

public sealed record AttachmentManifestItem(
    string AttachmentId,
    string FileName,
    long? SizeBytes,
    string? ContentSha256,
    string? MediaType,
    string? ArtifactId,
    string? ProcessorIdentity,
    string? ProcessorVersion,
    AttachmentProcessingStatus ProcessingStatus,
    bool CacheReused);

public sealed record AnalysisContextSnapshot(
    string SnapshotId,
    string SchemaVersion,
    string NormalizedEvidenceSchemaVersion,
    string WorkItemId,
    string EvaluatedRevision,
    string Organization,
    string Project,
    EvaluationEvidence Evidence,
    IReadOnlyList<AttachmentManifestItem> AttachmentManifest,
    string SourceSemanticFingerprint,
    string AttachmentManifestFingerprint,
    string EvidenceFingerprint,
    string PromptVersion,
    string ProfileId,
    string? ProfileVersion,
    string PolicyId,
    string PolicyVersion,
    string PolicyFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public IReadOnlyList<string> ProcessingWarnings { get; init; } = [];
}

public sealed record AttachmentEvidenceArtifact(
    string ArtifactId,
    string Organization,
    string Project,
    string AttachmentId,
    string FileName,
    string MediaType,
    long SourceByteSize,
    string ContentSha256,
    string ProcessorIdentity,
    string ProcessorVersion,
    string NormalizedEvidenceSchemaVersion,
    string ProcessingLimitsFingerprint,
    string NormalizedEvidence,
    AttachmentProcessingStatus ProcessingStatus,
    AttachmentInspectionMode InspectionMode,
    bool Truncated,
    bool Sampled,
    int? PagesAvailable,
    int? PagesInspected,
    string? FailureCategory,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public IReadOnlyDictionary<string, int> RedactionCategoryCounts { get; init; } = new Dictionary<string, int>();
    public VideoEvidenceMetadata? Video { get; init; }
    public IReadOnlyList<SelectedKeyScreenshot> SelectedKeyScreenshots { get; init; } = [];
}

// PR 2 extension contracts. This phase deliberately creates no media artifacts.
public sealed record VideoEvidenceMetadata(
    double? DurationSeconds,
    int? Width,
    int? Height,
    string? MediaProcessorVersion,
    IReadOnlyList<TranscriptSegment> Transcript,
    IReadOnlyList<VisualObservation> VisualObservations,
    FrameSamplingMetadata? FrameSampling);
public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text, string Provider, string Model, string Version, IReadOnlyList<string> Warnings);
public sealed record VisualObservation(double TimestampSeconds, string Observation, string Provider, string Model, string Version, string FrameIdentity);
public sealed record FrameSamplingMetadata(string Strategy, int FramesConsidered, int FramesInspected, bool Truncated);
public sealed record SelectedKeyScreenshot(string SourceAttachmentId, string SourceContentSha256, double TimestampSeconds, string DerivedSha256, int Width, int Height, long SizeBytes, string Provenance, string StorageReference, DateTimeOffset ExpiresAtUtc);

public sealed record ReusableEvaluation(
    string EquivalenceKey,
    string OriginEvaluationId,
    Guid OriginRunId,
    EvaluationResult Result,
    string? ProviderReportedModel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record EvidenceCleanupResult(int AnalysisContextsDeleted, int AttachmentArtifactsDeleted, int EvaluationEntriesDeleted, int ScreenshotReferencesDeleted);

public interface IAnalysisCacheRepository
{
    Task<AttachmentEvidenceArtifact?> GetAttachmentAsync(string artifactId, string organization, string project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task SaveAttachmentAsync(AttachmentEvidenceArtifact artifact, CancellationToken cancellationToken = default);
    Task<ReusableEvaluation?> GetEvaluationAsync(string equivalenceKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task SaveEvaluationAsync(ReusableEvaluation evaluation, CancellationToken cancellationToken = default);
    Task SaveContextAsync(AnalysisContextSnapshot context, CancellationToken cancellationToken = default);
    Task<AnalysisContextSnapshot?> GetContextAsync(string snapshotId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<EvidenceCleanupResult> DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public sealed class NullAnalysisCacheRepository : IAnalysisCacheRepository
{
    public Task<AttachmentEvidenceArtifact?> GetAttachmentAsync(string artifactId, string organization, string project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<AttachmentEvidenceArtifact?>(null);
    public Task SaveAttachmentAsync(AttachmentEvidenceArtifact artifact, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ReusableEvaluation?> GetEvaluationAsync(string equivalenceKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<ReusableEvaluation?>(null);
    public Task SaveEvaluationAsync(ReusableEvaluation evaluation, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveContextAsync(AnalysisContextSnapshot context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AnalysisContextSnapshot?> GetContextAsync(string snapshotId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisContextSnapshot?>(null);
    public Task<EvidenceCleanupResult> DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(new EvidenceCleanupResult(0, 0, 0, 0));
}

public static class AnalysisFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    public static string Sha256(string content) => Sha256(Encoding.UTF8.GetBytes(content));

    public static string ProcessingLimits(AttachmentLimits limits, int maximumExtractedCharacters) => Hash(new
    {
        limits.MaximumCount,
        limits.MaximumBytesPerAttachment,
        limits.MaximumAggregateBytes,
        limits.MaximumPdfPages,
        limits.MaximumImageCount,
        limits.MaximumImageBytes,
        limits.MaximumCsvRows,
        limits.MaximumStructuredTextDepth,
        MaximumExtractedCharacters = maximumExtractedCharacters
    });

    public static string Source(EvaluationEvidence evidence, IntakeStateConfiguration intakeState) => Hash(new
    {
        evidence.WorkItemId,
        evidence.WorkItemType,
        evidence.Title,
        Fields = evidence.Fields.Where(field => IsEvaluationRelevantField(field.ReferenceName)).ToArray(),
        evidence.Description,
        Tags = evidence.Tags.Where(tag => !string.Equals(tag, intakeState.ValidatedTag, StringComparison.OrdinalIgnoreCase) &&
                                          !string.Equals(tag, intakeState.IncompleteTag, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray(),
        evidence.Relations,
        evidence.Comments,
        evidence.Processing,
        evidence.Redaction
    });

    private static readonly HashSet<string> NonSemanticAdoFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Tags", "System.Rev", "System.ChangedDate", "System.ChangedBy", "System.Watermark"
    };

    public static bool IsEvaluationRelevantField(string referenceName) => !NonSemanticAdoFields.Contains(referenceName);

    public static string Manifest(IReadOnlyList<AttachmentManifestItem> manifest) => Hash(manifest
        .OrderBy(item => item.AttachmentId, StringComparer.Ordinal)
        .ThenBy(item => item.FileName, StringComparer.Ordinal)
        .Select(item => new
        {
            item.AttachmentId,
            item.FileName,
            item.SizeBytes,
            item.ContentSha256,
            item.MediaType,
            item.ArtifactId,
            item.ProcessorIdentity,
            item.ProcessorVersion,
            item.ProcessingStatus
        }).ToArray());

    public static string Evidence(string sourceFingerprint, string manifestFingerprint) =>
        Sha256($"{AnalysisContextVersions.NormalizedEvidence}|{sourceFingerprint}|{manifestFingerprint}");

    public static string Evaluation(AnalysisContextSnapshot context, DeploymentConfiguration configuration) => Hash(new
    {
        context.SourceSemanticFingerprint,
        context.AttachmentManifestFingerprint,
        context.NormalizedEvidenceSchemaVersion,
        ProfileId = configuration.Profile.Identity.Id,
        ProfileVersion = configuration.Profile.Identity.Version,
        configuration.PolicyFingerprint,
        PromptVersion = EvaluatorPrompt.Version,
        configuration.Profile.Ai.Provider,
        configuration.Profile.Ai.Model,
        Contract = AnalysisContextVersions.EvaluationContract,
        Processors = context.AttachmentManifest.Select(item => new { item.ProcessorIdentity, item.ProcessorVersion }).Distinct().OrderBy(item => item.ProcessorIdentity).ThenBy(item => item.ProcessorVersion).ToArray()
    });

    public static string Artifact(string organization, string project, string attachmentId, string contentSha256,
        string processorIdentity, string processorVersion, string limitsFingerprint,
        string normalizedEvidenceSchemaVersion = AnalysisContextVersions.NormalizedEvidence) => Sha256(string.Join('|',
        organization, project, attachmentId, contentSha256, processorIdentity, processorVersion,
        normalizedEvidenceSchemaVersion, limitsFingerprint));

    private static string Hash<T>(T value) => Sha256(JsonSerializer.Serialize(value, JsonOptions));
}

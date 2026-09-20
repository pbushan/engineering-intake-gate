using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Host.Operations;

public sealed record AnalyzeWorkItemRequest(int? WorkItemId, string? WorkItemUrl, bool ForceFresh = false);

public sealed record RunExecutionResponse(
    Guid RunId,
    RunTriggerType InvocationType,
    OperationalRunStatus Status,
    ExecutionMode EffectiveMode,
    long ConfigurationGenerationId,
    string DetailUrl);

public sealed record AnalyzeWorkItemResponse(
    Guid RunId,
    string? EvaluationId,
    int WorkItemId,
    OperationalDecisionState Decision,
    string DecisionLabel,
    WorkItemEligibility Eligibility,
    EvaluationProcessingStatus ProcessingStatus,
    ExecutionMode EffectiveMode,
    long ConfigurationGenerationId,
    string DetailUrl,
    string? ItemDetailUrl);

public enum OperationalDecisionState
{
    Pass,
    Fail,
    Error,
    NotEligible
}

public enum OperationalActorType
{
    User,
    System,
    HistoricalUnknown
}

public sealed record OperationalActorResponse(
    OperationalActorType Type,
    Guid? UserId,
    string? Username);

public sealed record RunHistoryPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<RunSummaryResponse> Items);

public sealed record RunSummaryResponse(
    Guid RunId,
    RunTriggerType InvocationType,
    OperationalActorResponse TriggeredBy,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds,
    ExecutionMode EffectiveMode,
    OperationalRunStatus Status,
    string ProfileId,
    long? ConfigurationGenerationId,
    int TicketsDiscovered,
    int TicketsEvaluated,
    int EngineeringReadyCount,
    int IntakeIncompleteCount,
    int ErrorCount,
    int NotEligibleCount,
    int SkippedCount,
    int DuplicateUpdatesSuppressedCount,
    int AzureDevOpsMutationCount,
    TokenUsageResponse? TokenUsage,
    EstimatedCostResponse? EstimatedCost,
    IReadOnlyList<SafeOperationalErrorResponse> Errors);

public sealed record RunDetailResponse(
    RunSummaryResponse Summary,
    string? ProfileVersion,
    Guid? SavedQueryId,
    string? SavedQueryFingerprint,
    string PolicyVersion,
    string PolicyFingerprint,
    string? ConfigurationFingerprint,
    IReadOnlyList<RunItemSummaryResponse> Items);

public sealed record RunItemSummaryResponse(
    string EvaluationId,
    string WorkItemId,
    Uri? AzureDevOpsUrl,
    OperationalDecisionState Decision,
    string DecisionLabel,
    WorkItemEligibility Eligibility,
    EvaluationProcessingStatus ProcessingStatus,
    bool UpdateSuppressed,
    long? ConfigurationGenerationId,
    TokenUsageResponse? TokenUsage,
    EstimatedCostResponse? EstimatedCost);

public sealed record RunItemDetailResponse(
    string EvaluationId,
    Guid RunId,
    Guid? ParentRunId,
    string WorkItemId,
    string EvaluatedRevision,
    Uri? AzureDevOpsUrl,
    OperationalDecisionState Decision,
    string DecisionLabel,
    WorkItemEligibility Eligibility,
    string? EligibilityReason,
    EvaluationProcessingStatus ProcessingStatus,
    ExecutionMode EffectiveMode,
    long? ConfigurationGenerationId,
    string SelectionReason,
    string PolicyVersion,
    string PolicyFingerprint,
    string PromptVersion,
    string Provider,
    string Model,
    IReadOnlyList<string> ApplicableCriteria,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<EvaluationDeficiency> MissingCriteria,
    IReadOnlyList<EvaluationAmbiguity> Ambiguities,
    string? EngineeringSummary,
    IReadOnlyList<EffectResponse> ProposedEffects,
    IReadOnlyList<MutationAttemptResponse> MutationAttempts,
    IReadOnlyList<EffectResponse> ActualEffects,
    bool UpdateSuppressed,
    string? SuppressionReason,
    bool? MateriallyChanged,
    TokenUsageResponse? TokenUsage,
    EstimatedCostResponse? EstimatedCost,
    IReadOnlyList<SafeOperationalErrorResponse> Errors)
{
    public StructuredTicketSummaryResponse TicketSummary { get; init; } = StructuredTicketSummaryResponse.Empty;
    public AnalysisContextResponse? AnalysisContext { get; init; }
    public IReadOnlyList<AttachmentProcessingResponse> AttachmentProcessing { get; init; } = [];
    public AiObservabilityResponse Ai { get; init; } = AiObservabilityResponse.Empty;
    public PlannedAdoMutationResponse? PlannedAdoMutation { get; init; }
    public bool ReusableEvidenceAvailable { get; init; }
    public DateTimeOffset? ReusableEvidenceExpiresAtUtc { get; init; }
    public IReadOnlyList<KeyVideoEvidenceResponse> KeyVideoEvidence { get; init; } = [];
}

public sealed record StructuredTicketSummaryResponse(
    string? IssueSummary,
    string? ExpectedBehavior,
    string? ActualBehavior,
    IReadOnlyList<string> ReproductionSteps,
    IReadOnlyList<string> AffectedExamples,
    string? Environment,
    string? BusinessImpact,
    IReadOnlyList<string> AttachmentFindings,
    IReadOnlyList<string> InvestigationWarnings)
{
    public static StructuredTicketSummaryResponse Empty { get; } = new(null, null, null, [], [], null, null, [], []);
}

public sealed record AnalysisContextResponse(
    string SnapshotId,
    string SchemaVersion,
    string NormalizedEvidenceSchemaVersion,
    string EvaluatedRevision,
    IReadOnlyList<string> SourceFields,
    int HumanCommentsIncluded,
    int HumanCommentsAvailable,
    int GeneratedCommentsExcluded,
    int AttachmentCount,
    bool TruncationOccurred,
    bool RedactionOccurred,
    int RedactionCount,
    IReadOnlyList<string> ProcessingWarnings,
    DateTimeOffset ExpiresAtUtc);

public sealed record AttachmentProcessingResponse(
    string AttachmentId,
    string FileName,
    string? MediaType,
    long? SizeBytes,
    AttachmentProcessingStatus Status,
    AttachmentInspectionMode InspectionMode,
    string? ProcessorIdentity,
    string? ProcessorVersion,
    bool CacheReused,
    bool Truncated,
    bool Sampled,
    int? PagesAvailable,
    int? PagesInspected,
    string? FailureCategory,
    IReadOnlyList<string> Warnings,
    string NormalizedEvidencePreview)
{
    public PdfEvidenceMetadata? Pdf { get; init; }
    public AudioEvidenceMetadata? Audio { get; init; }
    public VideoEvidenceMetadata? Video { get; init; }
}

public sealed record KeyVideoEvidenceResponse(
    string ScreenshotId,
    string SourceVideoFileName,
    double TimestampSeconds,
    string Observation,
    string Provenance,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset ExpiresAtUtc,
    string ArtifactUrl);

public sealed record AiObservabilityResponse(
    string ConfiguredProvider,
    string ConfiguredModel,
    string? ProviderReportedModel,
    string PromptVersion,
    int RunInteractions,
    int AttachmentArtifactsReused,
    int AttachmentArtifactsRegenerated,
    bool EvaluationReused,
    string? OriginEvaluationId,
    Guid? OriginRunId,
    AnalysisExecutionMode ExecutionMode)
{
    public IReadOnlyList<string> ProviderRequestIds { get; init; } = [];
    public static AiObservabilityResponse Empty { get; } = new("", "", null, "", 0, 0, 0, false, null, null, AnalysisExecutionMode.NormalReuseEligible);
}

public sealed record PlannedAdoMutationResponse(
    string PlanId,
    string EvaluatedRevision,
    IReadOnlyList<string> TagAdditions,
    IReadOnlyList<string> TagRemovals,
    string? ExactCommentBody,
    IReadOnlyList<string> FutureDerivedAttachmentUploads,
    string ContentFingerprint);

public sealed record EffectResponse(
    ProposedMutationType Type,
    string Target,
    string? Tag);

public sealed record MutationAttemptResponse(
    ProposedMutationType Type,
    string Target,
    string? Tag,
    bool Succeeded,
    DateTimeOffset? AttemptedAtUtc,
    int? ProviderStatusCode,
    string? SafeErrorCategory);

public sealed record TokenUsageResponse(int InputTokens, int OutputTokens, int TotalTokens);

public sealed record EstimatedCostResponse(
    decimal Amount,
    string Currency,
    string? PricingIdentity,
    bool Complete,
    int PricedInteractions,
    int TotalInteractions);

public sealed record SafeOperationalErrorResponse(string Category, string Message);

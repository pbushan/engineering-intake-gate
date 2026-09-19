using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.AiPricing;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Application.Audit;

public enum RunTriggerType
{
    Scheduled,
    ManualIncremental,
    ManualWorkItem
}

public sealed record TokenUsage(int InputTokens, int OutputTokens, int TotalTokens);
public sealed record EstimatedCost(decimal Amount, string Currency)
{
    public string? PricingIdentity { get; init; }
    public bool Complete { get; init; } = true;
    public int PricedInteractions { get; init; } = 1;
    public int TotalInteractions { get; init; } = 1;
}

public sealed record AppliedAiPricing(
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens,
    string Currency,
    string Source,
    Uri? SourceUri,
    string CatalogVersion,
    DateTimeOffset? EffectiveAtUtc,
    DateTimeOffset VerifiedAtUtc,
    AiModelPricingSourceKind SourceKind,
    bool Stale);

public sealed record AiInteractionCostRecord(
    int Attempt,
    string RequestedProviderIdentifier,
    string RequestedModelIdentifier,
    string ProviderUsedForPricing,
    string ModelUsedForPricing,
    string? ProviderRequestId,
    TokenUsage? TokenUsage,
    decimal? EstimatedInputCost,
    decimal? EstimatedOutputCost,
    decimal? EstimatedTotalCost,
    AppliedAiPricing? Pricing);
public sealed record RedactionCategoryCount(string Category, int Count);
public sealed record MutationOutcome(ProposedMutationType Type, bool Succeeded, string? SafeErrorCategory)
{
    public DateTimeOffset? AttemptedAtUtc { get; init; }
    public int? ProviderStatusCode { get; init; }
    public string? SafeTarget { get; init; }
}

public enum MutationExecutionState
{
    NotAttempted,
    Completed,
    StaleReevaluationRequired,
    Failed
}

/// <summary>Small provider-neutral classification vocabulary for operational decisions and audit.</summary>
public enum OperationalFailureCategory
{
    Transient,
    Permanent,
    StaleConcurrency,
    PersistenceUnavailable,
    PartialMutation,
    InvalidProviderOutput
}

public enum ReconciliationStatus
{
    None,
    Pending,
    Completed,
    StaleReevaluationRequired,
    Error
}

/// <summary>Safe, append-only-at-the-application-level state for a partially applied LIVE decision.</summary>
public sealed record MutationReconciliationRecord(
    string EvaluationId,
    string ProfileId,
    string WorkItemId,
    string EvaluatedRevision,
    string ExpectedRevision,
    IReadOnlyList<ProposedMutation> IntendedMutations,
    IReadOnlyList<string> DesiredTags,
    IReadOnlyList<ProposedMutationType> CompletedMutationTypes,
    ReconciliationStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int AttemptCount,
    string? FailureCategory)
{
    public IReadOnlyList<MutationOutcome> Outcomes { get; init; } = [];
}

/// <summary>Immutable audit snapshot retained each time mutation state is durably observed.</summary>
public sealed record MutationAuditEvent(
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<ProposedMutation> AttemptedMutations,
    IReadOnlyList<MutationOutcome> MutationOutcomes,
    MutationExecutionState MutationState,
    IReadOnlyList<string> ErrorCategories);
public sealed record AttachmentProcessingAuditRecord(
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
    string? FailureCategory);

public sealed record RunAuditRecord(
    Guid RunId,
    RunTriggerType TriggerType,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ExecutionMode ExecutionMode,
    string ProfileId,
    string PolicyVersion,
    string PolicyFingerprint,
    EvaluationProcessingStatus ProcessingStatus,
    int ItemCount,
    int PassCount,
    int FailCount,
    int ErrorCount,
    int SkippedCount,
    TokenUsage? TokenUsage,
    EstimatedCost? EstimatedCost,
    string? ErrorCategory)
{
    public long? ConfigurationGenerationId { get; init; }
    public Guid? SavedQueryId { get; init; }
    public WorkItemEligibility Eligibility { get; init; } = WorkItemEligibility.Eligible;
    public AuditActor? TriggeredBy { get; init; }
    /// <summary>The containing incremental discovery run, when this is an item evaluation.</summary>
    public Guid? ParentRunId { get; init; }
}

public sealed record EvaluationAuditRecord(
    string EvaluationId,
    Guid RunId,
    string WorkItemId,
    string EvaluatedRevision,
    DateTimeOffset EvaluatedAtUtc,
    string SelectionReason,
    string ProfileId,
    string PolicyId,
    string PolicyVersion,
    string PolicyFingerprint,
    string PromptVersion,
    string ProviderIdentifier,
    string ModelIdentifier,
    IReadOnlyList<string> ApplicableCriteria,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<EvaluationDeficiency> Deficiencies,
    IReadOnlyList<EvaluationAmbiguity> Ambiguities,
    string? EngineeringSummary,
    bool RedactionOccurred,
    int RedactionCount,
    IReadOnlyList<RedactionCategoryCount> RedactionCategories,
    int IncludedAttachmentMetadataCount,
    int AvailableAttachmentMetadataCount,
    int TruncatedAttachmentMetadataCount,
    int OmittedAttachmentMetadataCount,
    bool AttachmentMetadataAvailable,
    bool AttachmentContentInspected,
    IntakeDecision? Decision,
    EvaluationProcessingStatus ProcessingStatus,
    ExecutionMode ExecutionMode,
    TokenUsage? TokenUsage,
    EstimatedCost? EstimatedCost,
    IReadOnlyList<ProposedMutation> ProposedMutations,
    IReadOnlyList<ProposedMutation> AttemptedMutations,
    IReadOnlyList<MutationOutcome> MutationOutcomes,
    IReadOnlyList<string> ErrorCategories)
{
    public long? ConfigurationGenerationId { get; init; }
    public string? ProviderReportedModel { get; init; }
    public IReadOnlyList<string> ProviderRequestIds { get; init; } = [];
    public IReadOnlyList<AttachmentProcessingAuditRecord> AttachmentProcessing { get; init; } = [];
    public Guid? SavedQueryId { get; init; }
    public WorkItemEligibility Eligibility { get; init; } = WorkItemEligibility.Eligible;
    public string? ExclusionReason { get; init; }
    public MutationExecutionState MutationState { get; init; } = MutationExecutionState.NotAttempted;
    public ReconciliationStatus ReconciliationStatus { get; init; } = ReconciliationStatus.None;
    public IReadOnlyList<MutationAuditEvent> MutationAuditEvents { get; init; } = [];
    public AuditActor? TriggeredBy { get; init; }
    /// <summary>The containing incremental discovery run, when this evaluation belongs to one.</summary>
    public Guid? ParentRunId { get; init; }
    public bool UpdateSuppressed { get; init; }
    public string? SuppressionReason { get; init; }
    public bool? MateriallyChanged { get; init; }
    public IReadOnlyList<AiInteractionCostRecord> AiInteractions { get; init; } = [];
}

using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Host.Operations;

public sealed record AnalyzeWorkItemRequest(int? WorkItemId, string? WorkItemUrl);

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
    IReadOnlyList<SafeOperationalErrorResponse> Errors);

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

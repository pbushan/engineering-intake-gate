using IntakeGate.Application.Configuration;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.Evaluation;

/// <summary>Application-owned, provider-neutral boundary for untrusted structured AI output.</summary>
public interface IIntakeAiProvider
{
    Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken);
}

public sealed record AiProviderResponse(
    string? StructuredPayload,
    AiProviderFailure? Failure,
    AiProviderAttemptMetadata? Metadata = null)
{
    public static AiProviderResponse Success(string structuredPayload, AiProviderAttemptMetadata? metadata = null) => new(structuredPayload, null, metadata);
    public static AiProviderResponse Failed(AiProviderFailure failure, AiProviderAttemptMetadata? metadata = null) => new(null, failure, metadata);
}

public sealed record AiProviderAttemptMetadata(
    string? ProviderRequestId,
    string? ProviderReportedModel,
    TokenUsage? TokenUsage);

public sealed record AiProviderFailure(AiProviderFailureKind Kind, string SafeCategory);

public enum AiProviderFailureKind
{
    Transient,
    Permanent
}

public sealed record EvaluationRequest(
    string EvaluationId,
    string PromptVersion,
    string Prompt,
    EvaluationEvidence Evidence,
    string PolicyId,
    string PolicyVersion,
    string PolicyFingerprint,
    EvaluationProfileContext Profile,
    IReadOnlyList<EvaluationCriterion> Criteria,
    string ProviderIdentifier,
    string ModelIdentifier)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<VisualEvidence> VisualEvidence { get; init; } = [];
}

public sealed record EvaluationProfileContext(string ProfileId, string? ProfileVersion);

public sealed record EvaluationCriterion(
    string Id,
    string DisplayName,
    string Description,
    CriterionApplicability Applicability,
    NotApplicablePolicy NotApplicable,
    string EvaluationGuidance);

public enum IntakeDecision
{
    Pass,
    Fail
}

public enum EvaluationProcessingStatus
{
    Completed,
    Error
}

public sealed record EvaluationDeficiency(string CriterionId, string Reason, string RequiredSupportAction);

public sealed record EvaluationAmbiguity(string? CriterionId, string Description, string RequiredClarification);

/// <summary>Trusted result available only after deterministic parsing and contract validation.</summary>
public sealed record EvaluationResult(
    string EvaluationId,
    IntakeDecision Decision,
    string PolicyId,
    string PolicyVersion,
    string PolicyFingerprint,
    string PromptVersion,
    IReadOnlyList<string> ApplicableCriteria,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<EvaluationDeficiency> Deficiencies,
    IReadOnlyList<EvaluationAmbiguity> Ambiguities,
    string EngineeringSummary,
    string ProviderIdentifier,
    string ModelIdentifier);

public sealed record EvaluationProcessingResult(
    EvaluationProcessingStatus ProcessingStatus,
    EvaluationResult? Result,
    EvaluationFailure? Failure,
    int Attempts)
{
    public IntakeDecision? Outcome => Result?.Decision;
    public TokenUsage? TokenUsage { get; init; }
    public string? ProviderReportedModel { get; init; }
    public IReadOnlyList<string> ProviderRequestIds { get; init; } = [];
}

public sealed record EvaluationFailure(string Category);

public interface IIntakeEvaluationService
{
    Task<EvaluationProcessingResult> EvaluateAsync(
        EvaluationEvidence evidence,
        DeploymentConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<EvaluationProcessingResult> EvaluateAsync(
        EvaluationEvidence evidence,
        DeploymentConfiguration configuration,
        string evaluationId,
        CancellationToken cancellationToken = default);
}

public interface IEvaluationLog
{
    void EvaluationAttempt(EvaluationLogEntry entry);
}

public sealed record EvaluationLogEntry(
    string EvaluationId,
    string ProviderIdentifier,
    string ModelIdentifier,
    string PromptVersion,
    string PolicyFingerprint,
    int Attempt,
    EvaluationProcessingStatus ProcessingStatus,
    IntakeDecision? Decision,
    string? FailureCategory,
    bool WillRetry)
{
    public string? ProviderReportedModel { get; init; }
    public string? ProviderRequestId { get; init; }
    public TokenUsage? TokenUsage { get; init; }
    public long LatencyMilliseconds { get; init; }
}

public sealed class NullEvaluationLog : IEvaluationLog
{
    public void EvaluationAttempt(EvaluationLogEntry entry) { }
}

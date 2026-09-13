using IntakeGate.Application.Discovery;

namespace IntakeGate.Application.Audit;

public enum OperationalRunStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Error
}

public sealed record OperationalRunQuery(
    int Page,
    int PageSize,
    DateTimeOffset? StartedFromUtc,
    DateTimeOffset? StartedToUtc,
    RunTriggerType? TriggerType,
    OperationalRunStatus? Status,
    string? WorkItemId);

/// <summary>
/// Safe persisted audit envelope used by the application/host read-model projection. It contains
/// no raw evidence, provider body, prompt, attachment bytes, or credential material.
/// </summary>
public sealed record OperationalRunAuditEnvelope(
    RunAuditRecord? ItemRun,
    IncrementalRunAuditRecord? IncrementalRun,
    IReadOnlyList<EvaluationAuditRecord> Evaluations)
{
    public Guid RunId => IncrementalRun?.RunId ?? ItemRun!.RunId;
}

public sealed record OperationalRunAuditPage(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<OperationalRunAuditEnvelope> Runs);

public interface IOperationalAuditReader
{
    Task<OperationalRunAuditPage> ListRunsAsync(
        OperationalRunQuery query,
        CancellationToken cancellationToken = default);

    Task<OperationalRunAuditEnvelope?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<EvaluationAuditRecord?> GetEvaluationAsync(
        Guid runId,
        string evaluationId,
        CancellationToken cancellationToken = default);
}

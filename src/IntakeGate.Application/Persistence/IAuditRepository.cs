using IntakeGate.Application.Audit;

namespace IntakeGate.Application.Persistence;

/// <summary>Persists the run and its evaluation/proposed actions atomically before any future write is allowed.</summary>
public interface IAuditRepository
{
    Task SaveAsync(
        RunAuditRecord run,
        EvaluationAuditRecord evaluation,
        CancellationToken cancellationToken = default);

    Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default);

    Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Updates only the safe post-persistence mutation audit for an evaluation.</summary>
    Task UpdateMutationAuditAsync(
        string evaluationId,
        IReadOnlyList<Decision.ProposedMutation> attemptedMutations,
        IReadOnlyList<Audit.MutationOutcome> mutationOutcomes,
        Audit.MutationExecutionState mutationState,
        IReadOnlyList<string> errorCategories,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Mutation audit updates are not available from this repository.");

    /// <summary>Persists only an audit-proven material-change/duplicate-update determination.</summary>
    Task UpdateSuppressionAuditAsync(
        string evaluationId,
        bool updateSuppressed,
        string? suppressionReason,
        bool materiallyChanged,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Returns prior validator evaluations for one work item/profile; audit is authoritative for comment history.</summary>
    Task<IReadOnlyList<Audit.EvaluationAuditRecord>> GetWorkItemHistoryAsync(
        string workItemId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Audit.EvaluationAuditRecord>>([]);
}

using IntakeGate.Application.Audit;

namespace IntakeGate.Application.Persistence;

/// <summary>
/// Durable, provider-neutral record of a LIVE evaluation whose deterministic mutations have not
/// all been confirmed.  It is deliberately separate from logs: after a process crash this is the
/// sole authority used to decide whether a read-before-write reconciliation is safe.
/// </summary>
public interface IReconciliationRepository
{
    Task CreatePendingAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MutationReconciliationRecord>> GetPendingAsync(string profileId, CancellationToken cancellationToken = default);
    Task UpdateAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default);
}

using IntakeGate.Application.Audit;

namespace IntakeGate.Application.Discovery;

/// <summary>
/// The checkpoint is an application-owned marker for the last query whose selected work was
/// durably registered. It is deliberately not interpreted as an Azure DevOps changed-date.
/// </summary>
public sealed record DiscoveryCheckpoint(string ProfileId, DateTimeOffset AdvancedAtUtc, Guid DiscoveryRunId);

public enum RegisteredWorkState
{
    Pending,
    Processing,
    Completed,
    Error
}

public enum DiscoverySelectionReason
{
    NewlyDiscovered,
    IncompleteReevaluation,
    PendingRecovery,
    BootstrapOutsideLookback
}

public sealed record DiscoveryRegistrationRequest(int WorkItemId, DiscoverySelectionReason Reason, RegisteredWorkState InitialState);

public sealed record DiscoveredWorkRegistration(
    string ProfileId,
    int WorkItemId,
    Guid DiscoveryRunId,
    DateTimeOffset DiscoveredAtUtc,
    DiscoverySelectionReason SelectionReason,
    RegisteredWorkState ProcessingState,
    DateTimeOffset UpdatedAtUtc);

public enum IncrementalRunStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Error
}

/// <summary>Safe aggregate audit for a scheduled or Run Now discovery cycle.</summary>
public sealed record IncrementalRunAuditRecord(
    Guid RunId,
    RunTriggerType TriggerType,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Configuration.ExecutionMode ExecutionMode,
    string ProfileId,
    Guid SavedQueryId,
    DateTimeOffset? CheckpointBeforeUtc,
    DateTimeOffset? CheckpointAfterUtc,
    int QueryResultCount,
    int NewlyDiscoveredCount,
    int IncompleteReevaluationCount,
    int ProcessedCount,
    int PassCount,
    int FailCount,
    int ErrorCount,
    int SkippedCount,
    TokenUsage? TokenUsage,
    EstimatedCost? EstimatedCost,
    IncrementalRunStatus Status,
    string? ErrorCategory)
{
    public long? ConfigurationGenerationId { get; init; }
    public AuditActor? TriggeredBy { get; init; }
}

public sealed record ActiveRunLease(Guid RunId, DateTimeOffset AcquiredAtUtc, DateTimeOffset ExpiresAtUtc);

public interface IIncrementalDiscoveryRepository
{
    Task<DiscoveryCheckpoint?> GetCheckpointAsync(string profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveredWorkRegistration>> GetRegistrationsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveredWorkRegistration>> GetProcessableRegistrationsAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically upserts all selected registrations and advances the checkpoint. Callers must
    /// never advance a checkpoint through another path.
    /// </summary>
    Task RegisterAndAdvanceCheckpointAsync(
        string profileId,
        Guid discoveryRunId,
        IReadOnlyList<DiscoveryRegistrationRequest> selections,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task RegisterAndAdvanceCheckpointForGenerationAsync(
        string profileId,
        long configurationGenerationId,
        Guid discoveryRunId,
        IReadOnlyList<DiscoveryRegistrationRequest> selections,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        RegisterAndAdvanceCheckpointAsync(profileId, discoveryRunId, selections, nowUtc, cancellationToken);

    Task SetProcessingStateAsync(string profileId, int workItemId, RegisteredWorkState state, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task SetProcessingStateForGenerationAsync(string profileId, long configurationGenerationId,
        int workItemId, RegisteredWorkState state, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        SetProcessingStateAsync(profileId, workItemId, state, nowUtc, cancellationToken);
    Task SaveRunAuditAsync(IncrementalRunAuditRecord record, CancellationToken cancellationToken = default);
    Task<IncrementalRunAuditRecord?> GetRunAuditAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Acquires the one SQLite-backed per-profile lease, replacing only an expired lease.</summary>
    Task<ActiveRunLease?> TryAcquireRunLeaseAsync(string profileId, Guid runId, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<bool> RefreshRunLeaseAsync(string profileId, Guid runId, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task ReleaseRunLeaseAsync(string profileId, Guid runId, CancellationToken cancellationToken = default);
}

public sealed class RuntimeConfigurationChangedDuringDiscoveryException()
    : InvalidOperationException("The active runtime configuration changed during discovery; the old checkpoint was not advanced.");

public sealed record IncrementalRunResult(
    Guid? RunId,
    RunTriggerType TriggerType,
    IncrementalRunStatus? Status,
    Configuration.ExecutionMode ExecutionMode,
    DateTimeOffset? StartedAtUtc,
    bool Accepted,
    string? ErrorCategory)
{
    public static IncrementalRunResult Rejected(RunTriggerType trigger, Configuration.ExecutionMode mode) =>
        new(null, trigger, null, mode, null, false, "ActiveRunInProgress");
}

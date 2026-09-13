using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.WorkItems;

/// <summary>
/// Application-owned, read-only boundary for the governed work-item population. Provider SDK,
/// HTTP, authentication, and response types must remain behind this interface.
/// </summary>
public interface IWorkItemSource
{
    Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default);

    Task<WorkItemReadResult> GetWorkItemAsync(
        int workItemId,
        CancellationToken cancellationToken = default);
}

public enum WorkItemReadFailureKind
{
    Transient,
    Permanent
}

public sealed record WorkItemReadFailure(WorkItemReadFailureKind Kind, string SafeCategory);

public sealed record WorkItemQueryResult(
    IReadOnlyList<int> WorkItemIds,
    WorkItemReadFailure? Failure = null)
{
    public bool Succeeded => Failure is null;

    public static WorkItemQueryResult Failed(WorkItemReadFailure failure) => new([], failure);
}

public sealed record WorkItemReadResult(
    RawWorkItem? WorkItem,
    WorkItemReadFailure? Failure = null)
{
    public bool Succeeded => WorkItem is not null && Failure is null;

    public static WorkItemReadResult Failed(WorkItemReadFailure failure) => new(null, failure);
}

public enum WorkItemEligibility
{
    Eligible,
    NotEligible,
    Unknown
}

public sealed record ExclusionMatch(string RuleId, string FieldReference);

public interface IWorkItemEligibilityEvaluator
{
    ExclusionMatch? FindExclusion(RawWorkItem workItem, IReadOnlyList<Configuration.ExclusionRule> rules);
}

/// <summary>Safe, content-free events for discovery, eligibility, collection, and retry stages.</summary>
public interface IWorkItemReadLog
{
    void Discovery(Guid runId, int? workItemId, bool succeeded, int resultCount, string? failureCategory);
    void Eligibility(Guid runId, int workItemId, WorkItemEligibility eligibility, string? reason);
    void ContentCollection(Guid runId, int workItemId, string revision, int fieldCount, int commentCount, int relationCount, int attachmentCount);
    void ContentCollectionFailed(Guid runId, int workItemId, string failureCategory) { }
    void ExclusionFieldMissing(Guid runId, int workItemId, string ruleId, string fieldReference) { }
    void Retry(string operation, int? workItemId, int attempt, string failureCategory);
}

public sealed class NullWorkItemReadLog : IWorkItemReadLog
{
    public void Discovery(Guid runId, int? workItemId, bool succeeded, int resultCount, string? failureCategory) { }
    public void Eligibility(Guid runId, int workItemId, WorkItemEligibility eligibility, string? reason) { }
    public void ContentCollection(Guid runId, int workItemId, string revision, int fieldCount, int commentCount, int relationCount, int attachmentCount) { }
    public void Retry(string operation, int? workItemId, int attempt, string failureCategory) { }
}

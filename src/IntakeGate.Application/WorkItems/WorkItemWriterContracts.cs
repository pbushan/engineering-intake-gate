namespace IntakeGate.Application.WorkItems;

/// <summary>
/// Narrow application-owned mutation boundary.  It intentionally exposes only the two
/// intake-state operations that deterministic decision handling can request.
/// </summary>
public interface IWorkItemWriter
{
    Task<WorkItemMutationResult> UpdateIntakeTagsAsync(
        IntakeTagUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkItemMutationResult> AddValidatorCommentAsync(
        ValidatorCommentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record IntakeTagUpdateRequest(
    int WorkItemId,
    string ExpectedRevision,
    IReadOnlyList<string> FinalTags);

public sealed record ValidatorCommentRequest(
    int WorkItemId,
    string ExpectedRevision,
    string Body,
    string Marker);

public enum WorkItemMutationFailureKind
{
    Concurrency,
    Transient,
    Permanent
}

/// <summary>Safe transport result; it never contains provider response text or credentials.</summary>
public sealed record WorkItemMutationResult(
    bool Succeeded,
    string? ResultingRevision = null,
    int? ProviderStatusCode = null,
    WorkItemMutationFailureKind? FailureKind = null,
    string? SafeErrorCategory = null)
{
    public static WorkItemMutationResult Success(string? resultingRevision, int? statusCode = null) =>
        new(true, resultingRevision, statusCode);

    public static WorkItemMutationResult Failed(
        WorkItemMutationFailureKind kind,
        string category,
        int? statusCode = null) =>
        new(false, null, statusCode, kind, category);
}

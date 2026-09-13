namespace IntakeGate.Application.Audit;

public sealed record AuditActor(Guid Id, string Username)
{
    public static AuditActor System { get; } = new(Guid.Empty, "system");
}

public sealed record ControlPlaneAuditRecord(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActor Actor,
    string Operation,
    string TargetCategory,
    string TargetId,
    IReadOnlyList<string> ChangedFields);

public sealed record ControlPlaneAuditQuery(
    int Page,
    int PageSize,
    DateTimeOffset? OccurredFromUtc,
    DateTimeOffset? OccurredToUtc,
    string? Actor,
    string? Operation,
    string? TargetCategory,
    string? TargetId);

public sealed record ControlPlaneAuditPage(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<ControlPlaneAuditRecord> Records);

public interface IControlPlaneAuditRepository
{
    Task<IReadOnlyList<ControlPlaneAuditRecord>> ListControlPlaneAsync(
        CancellationToken cancellationToken = default);

    Task<ControlPlaneAuditPage> ListControlPlaneAsync(
        ControlPlaneAuditQuery query,
        CancellationToken cancellationToken = default);
}

public interface IControlPlaneAuditWriter
{
    Task AppendAsync(ControlPlaneAuditRecord record, CancellationToken cancellationToken = default);
}

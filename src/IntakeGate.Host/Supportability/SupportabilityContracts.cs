using IntakeGate.Application.Audit;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Host.Supportability;

public enum InfrastructureHealthStatus { Healthy, Unavailable }
public enum SetupHealthStatus { Ready, Incomplete }
public enum RuntimeHealthStatus { Active, ActivationFailed, NoActiveGeneration }
public enum ConnectionHealthStatus
{
    Verified,
    CredentialMissing,
    AuthenticationFailed,
    AuthorizationFailed,
    RateLimited,
    ProviderUnavailable,
    Timeout,
    NotVerified
}
public enum SchedulerHealthStatus { ManualOnly, Scheduled, Waiting, ActivationFailed }
public enum AuditActorDisplayType { User, System, Unknown }

public sealed record ApplicationHealthResponse(
    InfrastructureHealthStatus Status,
    string Application,
    string Version,
    string Environment);

public sealed record DatabaseHealthResponse(
    InfrastructureHealthStatus Status,
    bool Reachable,
    int CurrentSchemaVersion,
    bool MigrationCurrent);

public sealed record SetupHealthResponse(
    SetupHealthStatus Status,
    bool Complete,
    bool ProfileConfigured,
    bool SavedQueryConfirmed);

public sealed record RuntimeHealthResponse(
    RuntimeHealthStatus Status,
    long? ActiveGenerationId,
    bool ActivationCurrent);

public sealed record IntegrationHealthResponse(
    ConnectionHealthStatus Status,
    bool CredentialConfigured,
    CredentialVerificationStatus VerificationStatus,
    DateTimeOffset? LastVerifiedAtUtc,
    CredentialVerificationDiagnostic? VerificationDiagnostic,
    bool SettingsReady,
    string? Provider,
    string? Model);

public sealed record SchedulerHealthResponse(
    SchedulerHealthStatus Status,
    bool Scheduled,
    string? Timezone,
    DateTimeOffset? NextOccurrenceUtc,
    long? ActiveGenerationId);

public sealed record RecentFailureResponse(
    Guid RunId,
    DateTimeOffset OccurredAtUtc,
    OperationalRunStatus Status,
    string Category,
    string Message);

public sealed record AiDiagnosticMetadataResponse(
    DateTimeOffset? CredentialRevisionAtUtc,
    string? ActiveRuntimeProvider,
    string? ActiveRuntimeModel);

public sealed record SystemHealthResponse(
    DateTimeOffset GeneratedAtUtc,
    ApplicationHealthResponse Application,
    DatabaseHealthResponse Database,
    SetupHealthResponse Setup,
    RuntimeHealthResponse Runtime,
    IntegrationHealthResponse AzureDevOps,
    IntegrationHealthResponse Ai,
    SchedulerHealthResponse Scheduler,
    IReadOnlyList<RecentFailureResponse> RecentFailures,
    AiDiagnosticMetadataResponse? AiDiagnostics);

public sealed record AuditActorResponse(
    AuditActorDisplayType Type,
    Guid? UserId,
    string DisplayName);

public sealed record ControlPlaneAuditItemResponse(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActorResponse Actor,
    string Operation,
    string TargetCategory,
    string TargetId,
    IReadOnlyList<string> ChangedFields);

public sealed record ControlPlaneAuditPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<ControlPlaneAuditItemResponse> Items);

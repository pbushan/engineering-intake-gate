using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Application.AzureDevOps;

public enum AzureDevOpsManagementFailure
{
    ProfileNotConfigured,
    CredentialUnavailable,
    AuthenticationFailed,
    AuthorizationFailed,
    OrganizationOrProjectUnavailable,
    Timeout,
    ProviderUnavailable,
    QueryNotFoundOrInaccessible,
    InvalidProviderResponse,
    UnexpectedFailure
}

public sealed record AzureDevOpsConnectionResult(
    bool Succeeded,
    AzureDevOpsManagementFailure? Failure = null);

public sealed record AzureDevOpsQueryPreviewItem(
    int Id,
    string Title,
    string WorkItemType,
    string State,
    Uri WebUrl);

public sealed record AzureDevOpsQueryValidationResult(
    bool Succeeded,
    Guid QueryId,
    int TotalCount,
    IReadOnlyList<AzureDevOpsQueryPreviewItem> Preview,
    AzureDevOpsManagementFailure? Failure = null)
{
    public static AzureDevOpsQueryValidationResult Failed(Guid queryId, AzureDevOpsManagementFailure failure) =>
        new(false, queryId, 0, [], failure);
}

/// <summary>Application-owned management port. Provider HTTP and PAT handling remain in Infrastructure.</summary>
public interface IAzureDevOpsManagementClient
{
    Task<AzureDevOpsConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<AzureDevOpsQueryValidationResult> ValidateSavedQueryAsync(
        Guid queryId,
        CancellationToken cancellationToken = default);
}

public interface IAzureDevOpsManagementClientFactory
{
    IAzureDevOpsManagementClient Create(
        AzureDevOpsConfiguration configuration,
        SecretValue credential,
        int maximumRetries);
}

public sealed record AzureDevOpsConfigurationState(
    string ProfileId,
    int Generation,
    string Fingerprint,
    bool QueryConfirmed,
    DateTimeOffset? QueryValidatedAtUtc);

public sealed record AzureDevOpsSetupSettings(
    Uri OrganizationUrl,
    string Project,
    Guid? SavedQueryId,
    string? ConfigurationFingerprint,
    DateTimeOffset? QueryValidatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IAzureDevOpsSetupRepository
{
    Task<AzureDevOpsSetupSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        Uri organizationUrl,
        string project,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmQueryAsync(
        AzureDevOpsConfiguration configuration,
        string expectedCurrentFingerprint,
        string candidateFingerprint,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum AzureDevOpsConfigurationCommitStatus
{
    Committed,
    AlreadyCurrent,
    Conflict,
    ActiveRunInProgress,
    ProfileNotConfigured
}

public sealed record AzureDevOpsConfigurationCommitResult(
    AzureDevOpsConfigurationCommitStatus Status,
    int Generation,
    bool RestartRequired);

public interface IAzureDevOpsConfigurationRepository
{
    Task<AzureDevOpsConfigurationState?> GetStateAsync(CancellationToken cancellationToken = default);

    Task<AzureDevOpsConfigurationCommitResult> CommitValidatedAsync(
        DeploymentConfiguration candidate,
        string expectedCurrentFingerprint,
        string candidateFingerprint,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);
}

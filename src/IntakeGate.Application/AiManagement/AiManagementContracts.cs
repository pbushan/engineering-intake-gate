using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Application.AiManagement;

public static class AiProviderNames
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    public static bool TryNormalize(string? value, out string provider)
    {
        provider = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return provider is OpenAi or Anthropic;
    }

    public static CredentialSlot CredentialSlot(string provider) => provider switch
    {
        OpenAi => IntakeGate.Application.Secrets.CredentialSlot.OpenAi,
        Anthropic => IntakeGate.Application.Secrets.CredentialSlot.Anthropic,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

public enum AiManagementFailure
{
    CredentialUnavailable,
    CredentialNotVerified,
    CredentialChanged,
    AuthenticationFailed,
    AuthorizationFailed,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    ModelNotFound,
    InvalidProviderResponse,
    UnexpectedFailure
}

public sealed record AiModelDescriptor(string Provider, string Id, string? DisplayName);

public sealed record AiCredentialVerificationResult(bool Succeeded, AiManagementFailure? Failure = null);

public sealed record AiModelDiscoveryResult(
    bool Succeeded,
    IReadOnlyList<AiModelDescriptor> Models,
    AiManagementFailure? Failure = null);

public sealed record AiModelValidationResult(
    bool Succeeded,
    AiModelDescriptor? Model,
    AiManagementFailure? Failure = null);

/// <summary>Application-owned provider-management boundary. It never carries provider payloads.</summary>
public interface IAiManagementClient
{
    Task<AiCredentialVerificationResult> VerifyCredentialAsync(CancellationToken cancellationToken = default);
    Task<AiModelDiscoveryResult> DiscoverModelsAsync(CancellationToken cancellationToken = default);
    Task<AiModelValidationResult> ValidateModelAsync(string modelId, CancellationToken cancellationToken = default);
}

public interface IAiManagementClientFactory
{
    IAiManagementClient Create(string provider, SecretValue credential, TimeSpan timeout);
}

public sealed record AiConfigurationSettings(
    bool ProfileConfigured,
    string? ProfileId,
    string? Provider,
    string? Model,
    bool ModelConfirmed,
    DateTimeOffset? ModelValidatedAtUtc,
    DateTimeOffset? CredentialUpdatedAtUtc,
    string Fingerprint);

public enum AiConfigurationCommitStatus
{
    Committed,
    AlreadyCurrent,
    Conflict,
    ProfileStateChanged
}

public sealed record AiConfigurationCommitResult(
    AiConfigurationCommitStatus Status,
    bool ProfileConfigured,
    bool RestartRequired);

public interface IAiConfigurationRepository
{
    Task<AiConfigurationSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<AiConfigurationCommitResult> CommitValidatedAsync(
        DeploymentConfiguration? candidateConfiguration,
        string provider,
        string model,
        string expectedCurrentFingerprint,
        DateTimeOffset credentialUpdatedAtUtc,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);
}

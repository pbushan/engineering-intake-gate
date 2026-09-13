using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;

namespace IntakeGate.Application.Secrets;

public enum CredentialSlot
{
    AzureDevOps,
    OpenAi,
    Anthropic
}

public enum SecretSourceKind
{
    LocallyEncrypted,
    EnvironmentReference
}

public enum CredentialVerificationStatus
{
    NeverVerified,
    Verified,
    Failed
}

public enum CredentialVerificationDiagnostic
{
    AuthenticationRejected,
    PermissionDenied,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    Unknown
}

public sealed record CredentialMetadata(
    CredentialSlot Slot,
    bool Configured,
    SecretSourceKind? SourceKind,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    CredentialVerificationStatus VerificationStatus,
    DateTimeOffset? LastVerifiedAtUtc,
    CredentialVerificationDiagnostic? VerificationDiagnostic);

/// <summary>
/// Explicitly marks secret material returned only to trusted backend code. It has no serializable
/// value property and its string representation is always redacted.
/// </summary>
public sealed class SecretValue
{
    private readonly string value;

    public SecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        this.value = value;
    }

    public string DangerousGetValue() => value;

    public override string ToString() => "[SECRET]";
}

public enum SecretAvailability
{
    Available,
    NotConfigured,
    EnvironmentVariableUnavailable,
    DecryptionFailed
}

public sealed record SecretResolution(
    SecretAvailability Availability,
    [property: JsonIgnore] SecretValue? Secret)
{
    public static SecretResolution Available(SecretValue value) => new(SecretAvailability.Available, value);
    public static SecretResolution Unavailable(SecretAvailability availability) => new(availability, null);
}

public interface ISecretStore
{
    Task ValidateAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata> GetMetadataAsync(CredentialSlot slot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CredentialMetadata>> ListMetadataAsync(CancellationToken cancellationToken = default);
    Task<SecretResolution> ResolveAsync(CredentialSlot slot, CancellationToken cancellationToken = default);
    Task ReplaceLocalAsync(
        CredentialSlot slot,
        SecretValue replacement,
        AuditActor actor,
        CancellationToken cancellationToken = default);
    Task ConfigureEnvironmentReferenceAsync(
        CredentialSlot slot,
        string environmentVariableName,
        AuditActor actor,
        CancellationToken cancellationToken = default);
    Task SetVerificationAsync(
        CredentialSlot slot,
        CredentialVerificationStatus status,
        DateTimeOffset? verifiedAtUtc,
        CredentialVerificationDiagnostic? safeDiagnostic,
        AuditActor actor,
        CancellationToken cancellationToken = default);
    Task<bool> TrySetVerificationIfCurrentAsync(
        CredentialSlot slot,
        DateTimeOffset expectedUpdatedAtUtc,
        CredentialVerificationStatus status,
        DateTimeOffset? verifiedAtUtc,
        CredentialVerificationDiagnostic? safeDiagnostic,
        AuditActor actor,
        CancellationToken cancellationToken = default);
}

public sealed class SecretStoreOperationException(string safeCategory)
    : InvalidOperationException($"Secret store operation failed: {safeCategory}.")
{
    public string SafeCategory { get; } = safeCategory;
}

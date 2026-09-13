using IntakeGate.Application.Secrets;

namespace IntakeGate.Host;

public sealed record ApiErrorResponse(
    string Error,
    string Message,
    IReadOnlyList<string>? Details = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldErrors = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? SectionErrors = null);

public sealed record VersionResponse(string Application, string Version, string Environment);

public sealed record HealthResponse(string Status, string? SetupStatus = null);

public sealed record CredentialMetadataResponse(
    bool Configured,
    SecretSourceKind? SourceKind,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    CredentialVerificationStatus VerificationStatus,
    DateTimeOffset? LastVerifiedAtUtc,
    CredentialVerificationDiagnostic? VerificationDiagnostic);

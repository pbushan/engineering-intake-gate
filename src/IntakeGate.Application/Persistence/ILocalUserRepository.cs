using IntakeGate.Application.Authentication;
using IntakeGate.Application.Audit;

namespace IntakeGate.Application.Persistence;

/// <summary>Persistence boundary for the deployment's local Admin and Viewer accounts.</summary>
public interface ILocalUserRepository
{
    Task<bool> AnyUsersAsync(CancellationToken cancellationToken = default);

    Task<LocalUser?> FindByNormalizedUsernameAsync(
        string normalizedUsername,
        CancellationToken cancellationToken = default);

    Task<LocalUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalUser>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> TryBootstrapAdminAsync(
        Guid id,
        string username,
        string normalizedUsername,
        string? displayName,
        string passwordHash,
        DateTimeOffset nowUtc,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default);

    Task<bool> TryCreateAsync(
        LocalUser user,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplacePasswordHashAsync(
        Guid id,
        string expectedPasswordHash,
        string replacementPasswordHash,
        DateTimeOffset changedAtUtc,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default);
}

using IntakeGate.Application.Configuration;

namespace IntakeGate.Application.Persistence;

/// <summary>Persistence boundary for the deployment's zero-or-one authoritative profile.</summary>
public interface ISingletonProfileRepository
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);

    Task<DeploymentConfiguration?> LoadAsync(CancellationToken cancellationToken = default);

    Task CreateAsync(DeploymentConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class SingletonProfileAlreadyExistsException : InvalidOperationException
{
    public SingletonProfileAlreadyExistsException()
        : base("A deployment profile already exists in SQLite; legacy import is allowed only for an unconfigured installation.")
    {
    }
}

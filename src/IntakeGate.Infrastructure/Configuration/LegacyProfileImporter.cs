using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;

namespace IntakeGate.Infrastructure.Configuration;

/// <summary>Explicit one-time bridge from one operator-supplied YAML source into SQLite.</summary>
public sealed class LegacyProfileImporter(
    ISingletonProfileRepository repository,
    YamlDeploymentConfigurationLoader? loader = null)
{
    private readonly YamlDeploymentConfigurationLoader loader = loader ?? new YamlDeploymentConfigurationLoader();

    public async Task<DeploymentConfiguration> ImportAsync(
        string exactProfilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exactProfilePath))
            throw new ArgumentException("An exact legacy profile YAML path is required.", nameof(exactProfilePath));

        // Parse and validate both documents before opening the create-only persistence boundary.
        var configuration = loader.Load(exactProfilePath);
        await repository.CreateAsync(configuration, cancellationToken);
        return configuration;
    }
}

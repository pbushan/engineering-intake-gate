using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Secrets;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteRuntimeConfigurationGenerationRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-generations-{Guid.NewGuid():N}");

    [Fact]
    public async Task CFG_009_ActivationAppendsSecretFreeImmutableGenerationAndMovesPointerAtomically()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runtime.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var validator = new DeploymentConfigurationValidator();
        var configuration = new YamlDeploymentConfigurationLoader(validator).Load(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));
        var repository = new SqliteRuntimeConfigurationGenerationRepository(database, validator);
        var revision = DateTimeOffset.Parse("2026-09-12T12:00:00Z");

        var fingerprint = RuntimeConfigurationFingerprint.Create(configuration, revision, revision);
        var first = await repository.ActivateAsync(configuration, fingerprint,
            new RuntimeCredentialBinding(CredentialSlot.AzureDevOps, SecretSourceKind.LocallyEncrypted, revision),
            new RuntimeCredentialBinding(CredentialSlot.OpenAi, SecretSourceKind.EnvironmentReference, revision),
            new AuditActor(Guid.NewGuid(), "generation-admin"), revision, ["profile"], default);
        var active = await repository.GetActiveAsync();

        Assert.Equal(first.GenerationId, active!.GenerationId);
        Assert.Equal(first.GenerationId, active.Configuration.RuntimeGenerationId);
        Assert.Equal(fingerprint, active.ConfigurationFingerprint);
        var stored = await ScalarAsync<string>(database,
            "SELECT profile_json || policy_json FROM runtime_configuration_generations WHERE generation_id = 1;");
        Assert.DoesNotContain("credential-canary", stored, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runtime_configuration_generations SET configuration_fingerprint = 'mutated' WHERE generation_id = 1;";
            await command.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task CFG_016_OldGenerationCannotAdvanceDiscoveryAfterNewQueryGenerationActivates()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runtime-rebaseline.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var validator = new DeploymentConfigurationValidator();
        var firstConfiguration = new YamlDeploymentConfigurationLoader(validator).Load(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));
        await new SqliteSingletonProfileRepository(database).CreateAsync(firstConfiguration);
        var generations = new SqliteRuntimeConfigurationGenerationRepository(database, validator);
        var revision = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
        var binding = new RuntimeCredentialBinding(CredentialSlot.AzureDevOps,
            SecretSourceKind.EnvironmentReference, revision);
        var aiBinding = new RuntimeCredentialBinding(CredentialSlot.OpenAi,
            SecretSourceKind.EnvironmentReference, revision);
        var first = await generations.ActivateAsync(firstConfiguration,
            RuntimeConfigurationFingerprint.Create(firstConfiguration, revision, revision),
            binding, aiBinding, AuditActor.System, revision, ["profile"]);
        var secondConfiguration = firstConfiguration with
        {
            Profile = firstConfiguration.Profile with
            {
                Ado = firstConfiguration.Profile.Ado with { SavedQueryId = Guid.NewGuid() }
            }
        };
        var second = await generations.ActivateAsync(secondConfiguration,
            RuntimeConfigurationFingerprint.Create(secondConfiguration, revision, revision),
            binding, aiBinding, AuditActor.System, revision.AddMinutes(1), ["ado.savedQueryId"]);
        Assert.True(second.GenerationId > first.GenerationId);

        var discovery = new SqliteIncrementalDiscoveryRepository(database);
        await Assert.ThrowsAsync<RuntimeConfigurationChangedDuringDiscoveryException>(() =>
            discovery.RegisterAndAdvanceCheckpointForGenerationAsync(
                first.ProfileId, first.GenerationId, Guid.NewGuid(),
                [new DiscoveryRegistrationRequest(42, DiscoverySelectionReason.NewlyDiscovered, RegisteredWorkState.Pending)],
                revision.AddMinutes(2)));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM discovery_checkpoints;"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM discovered_work_registrations;"));
    }

    private static async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EngineeringIntakeGate.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

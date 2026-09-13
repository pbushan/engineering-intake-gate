using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteAzureDevOpsConfigurationRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-ado-config-{Guid.NewGuid():N}");
    private static readonly AuditActor Actor = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "phase3a-admin");

    [Fact]
    public async Task DISC_009_ConfirmedChangeAtomicallyRebaselinesOnlyQueryBoundDiscoveryState()
    {
        var (database, current, repository) = await CreateAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO discovery_checkpoints VALUES ('{current.Profile.Identity.Id}', '2026-09-12T12:00:00Z', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
            INSERT INTO discovered_work_registrations VALUES ('{current.Profile.Identity.Id}', 42, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '2026-09-12T12:00:00Z', 'NewlyDiscovered', 'Completed', '2026-09-12T12:00:00Z');
            INSERT INTO incremental_run_audits (run_id, started_at_utc, payload_json)
            VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', '2026-09-12T12:00:00Z', 'preserved');
            """);
        var candidate = current with
        {
            Profile = current.Profile with
            {
                Ado = current.Profile.Ado with
                {
                    SavedQueryId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")
                }
            }
        };
        var expected = AzureDevOpsConfigurationFingerprint.Create(current.Profile.Identity.Id, current.Profile.Ado);
        var fingerprint = AzureDevOpsConfigurationFingerprint.Create(candidate.Profile.Identity.Id, candidate.Profile.Ado);

        var result = await repository.CommitValidatedAsync(candidate, expected, fingerprint, Actor,
            DateTimeOffset.Parse("2026-09-12T13:00:00Z"));

        Assert.Equal(AzureDevOpsConfigurationCommitStatus.Committed, result.Status);
        Assert.False(result.RestartRequired);
        Assert.Equal(1, result.Generation);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM discovery_checkpoints;"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM discovered_work_registrations;"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM incremental_run_audits;"));
        Assert.Equal(current.Profile.Identity.Id, await ScalarAsync<string>(database,
            "SELECT profile_id FROM singleton_profile_configuration;"));
        Assert.Equal(candidate.Profile.Ado.SavedQueryId.ToString("D"), await ScalarAsync<string>(database,
            "SELECT saved_query_id FROM ado_configuration_state;"));
        var operations = await new SqliteAuditRepository(database).ListControlPlaneAsync();
        Assert.Contains(operations, item => item.Operation == "AzureDevOpsSavedQueryCandidateConfirmed");
        Assert.Contains(operations, item => item.Operation == "AzureDevOpsConfigurationChanged");
        Assert.Contains(operations, item => item.Operation == "AzureDevOpsDiscoveryRebaselined");
    }

    [Fact]
    public async Task DISC_009_StaleCandidateIsRejectedButActiveRunDoesNotBlockFutureGeneration()
    {
        var (database, current, repository) = await CreateAsync();
        var candidate = current with
        {
            Profile = current.Profile with
            {
                Ado = current.Profile.Ado with { SavedQueryId = Guid.NewGuid() }
            }
        };
        var candidateFingerprint = AzureDevOpsConfigurationFingerprint.Create(candidate.Profile.Identity.Id, candidate.Profile.Ado);
        var stale = await repository.CommitValidatedAsync(candidate, "sha256:stale", candidateFingerprint,
            Actor, DateTimeOffset.UtcNow);
        Assert.Equal(AzureDevOpsConfigurationCommitStatus.Conflict, stale.Status);

        await ExecuteAsync(database, $"""
            INSERT INTO active_run_leases VALUES ('{current.Profile.Identity.Id}', 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
                '2026-09-12T12:00:00Z', '2999-09-12T12:15:00Z');
            """);
        var expected = AzureDevOpsConfigurationFingerprint.Create(current.Profile.Identity.Id, current.Profile.Ado);
        var locked = await repository.CommitValidatedAsync(candidate, expected, candidateFingerprint,
            Actor, DateTimeOffset.UtcNow);

        Assert.Equal(AzureDevOpsConfigurationCommitStatus.Committed, locked.Status);
        var reloaded = await new SqliteSingletonProfileRepository(database).LoadAsync();
        Assert.Equal(candidate.Profile.Ado.SavedQueryId, reloaded!.Profile.Ado.SavedQueryId);
    }

    private async Task<(string Database, DeploymentConfiguration Configuration, SqliteAzureDevOpsConfigurationRepository Repository)> CreateAsync()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "intake-gate.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var root = RepositoryRoot();
        var configuration = new YamlDeploymentConfigurationLoader().Load(
            Path.Combine(root, "profiles", "example", "profile.yaml"));
        var validator = new DeploymentConfigurationValidator();
        await new SqliteSingletonProfileRepository(database, validator).CreateAsync(configuration);
        return (database, configuration, new SqliteAzureDevOpsConfigurationRepository(database, validator));
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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

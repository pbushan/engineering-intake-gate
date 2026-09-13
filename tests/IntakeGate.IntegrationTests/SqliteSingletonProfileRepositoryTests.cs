using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteSingletonProfileRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"intake-gate-profile-repository-{Guid.NewGuid():N}");

    [Fact]
    public async Task CFG_008_ProfilelessDatabaseHasNoAuthoritativeConfiguration()
    {
        var (path, repository) = await CreateRepositoryAsync();

        Assert.False(await repository.ExistsAsync());
        Assert.Null(await repository.LoadAsync());
        Assert.Equal(0L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM singleton_profile_configuration;"));
    }

    [Fact]
    public async Task CFG_008_ExplicitImportPreservesIdentityAndLoadsThroughSharedValidation()
    {
        var path = DatabasePath();
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var validator = new RecordingValidator();
        var repository = new SqliteSingletonProfileRepository(path, validator);
        var source = Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml");

        var imported = await new LegacyProfileImporter(repository).ImportAsync(source);
        var loaded = await repository.LoadAsync();

        Assert.Equal("example", imported.Profile.Identity.Id);
        Assert.Equal("example", loaded!.Profile.Identity.Id);
        Assert.Equal("example", await ScalarAsync<string>(path,
            "SELECT profile_id FROM singleton_profile_configuration WHERE singleton_id = 1;"));
        Assert.Equal(2, validator.ValidateCalls);
    }

    [Fact]
    public async Task CFG_008_FirstImportWinsAndSecondImportLeavesExistingProfileUnchanged()
    {
        var (path, repository) = await CreateRepositoryAsync();
        var root = RepositoryRoot();
        await new LegacyProfileImporter(repository).ImportAsync(Path.Combine(root, "profiles", "example", "profile.yaml"));
        var originalProfileJson = await ScalarAsync<string>(path, "SELECT profile_json FROM singleton_profile_configuration;");

        await Assert.ThrowsAsync<SingletonProfileAlreadyExistsException>(() =>
            new LegacyProfileImporter(repository).ImportAsync(Path.Combine(root, "profiles", "alternate", "profile.yaml")));

        Assert.Equal("example", (await repository.LoadAsync())!.Profile.Identity.Id);
        Assert.Equal(originalProfileJson,
            await ScalarAsync<string>(path, "SELECT profile_json FROM singleton_profile_configuration;"));
    }

    [Theory]
    [InlineData("profile: [unterminated", null)]
    [InlineData(null, "policy: [unterminated")]
    [InlineData(null, "policy:\n  id: invalid\n  version: '1'\ncriteria: []")]
    public async Task CFG_008_InvalidLegacyInputLeavesAuthoritativeProfileAbsent(string? profile, string? policy)
    {
        var (_, repository) = await CreateRepositoryAsync();
        using var source = LegacySource.Create(profile, policy);

        await Assert.ThrowsAnyAsync<ConfigurationValidationException>(() =>
            new LegacyProfileImporter(repository).ImportAsync(source.ProfilePath));

        Assert.False(await repository.ExistsAsync());
    }

    [Fact]
    public async Task CFG_008_MissingReferencedPolicyLeavesAuthoritativeProfileAbsent()
    {
        var (_, repository) = await CreateRepositoryAsync();
        using var source = LegacySource.Create();
        File.Delete(source.PolicyPath);

        await Assert.ThrowsAsync<ConfigurationDocumentException>(() =>
            new LegacyProfileImporter(repository).ImportAsync(source.ProfilePath));

        Assert.False(await repository.ExistsAsync());
    }

    [Fact]
    public async Task CFG_008_PersistenceFailureCannotCreatePartialAuthority()
    {
        var (path, repository) = await CreateRepositoryAsync();
        await ExecuteAsync(path, """
            CREATE TRIGGER fail_profile_insert
            BEFORE INSERT ON singleton_profile_configuration
            BEGIN SELECT RAISE(ABORT, 'synthetic persistence failure'); END;
            """);
        var configuration = new YamlDeploymentConfigurationLoader().Load(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));

        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateAsync(configuration));

        Assert.False(await repository.ExistsAsync());
    }

    [Fact]
    public async Task CFG_008_SQLiteRemainsAuthorityAfterLegacySourceIsChangedAndRemoved()
    {
        var (_, repository) = await CreateRepositoryAsync();
        using var source = LegacySource.Create();
        await new LegacyProfileImporter(repository).ImportAsync(source.ProfilePath);
        File.WriteAllText(source.ProfilePath, "profile: [now-invalid");
        File.Delete(source.PolicyPath);

        var loaded = await repository.LoadAsync();

        Assert.Equal("stable-profile", loaded!.Profile.Identity.Id);
        Assert.Equal("stable-policy", loaded.Policy.Identity.Id);
    }

    [Fact]
    public async Task CFG_008_CorruptPersistedConfigurationFailsExplicitlyWithoutSourceFallback()
    {
        var (path, repository) = await CreateRepositoryAsync();
        await ExecuteAsync(path, """
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, 'corrupt', '{not-json', '{}', '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
            """);

        await Assert.ThrowsAsync<ConfigurationDocumentException>(() => repository.LoadAsync());
        Assert.True(await repository.ExistsAsync());
    }

    [Fact]
    public async Task CFG_008_ConcurrentCreatesStillProduceExactlyOneLogicalProfile()
    {
        var path = DatabasePath();
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var configuration = new YamlDeploymentConfigurationLoader().Load(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));
        var attempts = new[]
        {
            new SqliteSingletonProfileRepository(path).CreateAsync(configuration),
            new SqliteSingletonProfileRepository(path).CreateAsync(configuration)
        };

        var outcomes = await Task.WhenAll(attempts.Select(async attempt =>
        {
            try { await attempt; return "created"; }
            catch (SingletonProfileAlreadyExistsException) { return "rejected"; }
        }));

        Assert.Equal(1, outcomes.Count(item => item == "created"));
        Assert.Equal(1, outcomes.Count(item => item == "rejected"));
        Assert.Equal(1L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM singleton_profile_configuration;"));
    }

    [Fact]
    public async Task CFG_008_ImportPreservesRuntimeStateAssociationsForTheSameProfileIdentity()
    {
        var (path, repository) = await CreateRepositoryAsync();
        await ExecuteAsync(path, """
            INSERT INTO discovery_checkpoints VALUES ('example', '2026-09-12T00:00:00Z', 'discovery-1');
            INSERT INTO discovered_work_registrations VALUES ('example', 42, 'discovery-1', '2026-09-12T00:00:00Z', 'NewlyDiscovered', 'Pending', '2026-09-12T00:00:00Z');
            INSERT INTO active_run_leases VALUES ('example', 'lease-1', '2026-09-12T00:00:00Z', '2026-09-12T00:15:00Z');
            INSERT INTO run_audits (run_id, started_at_utc, payload_json)
            VALUES ('run-1', '2026-09-12T00:00:00Z', '{"profileId":"example"}');
            INSERT INTO evaluation_audits (evaluation_id, run_id, evaluated_at_utc, payload_json)
            VALUES ('evaluation-1', 'run-1', '2026-09-12T00:00:01Z', '{"profileId":"example"}');
            INSERT INTO mutation_reconciliations VALUES ('evaluation-1', 'example', '42', 'Pending', '2026-09-12T00:00:02Z', '{}');
            """);

        await new LegacyProfileImporter(repository).ImportAsync(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));

        Assert.Equal("example", (await repository.LoadAsync())!.Profile.Identity.Id);
        foreach (var table in new[]
                 {
                     "discovery_checkpoints", "discovered_work_registrations", "active_run_leases",
                     "mutation_reconciliations"
                 })
        {
            Assert.Equal("example", await ScalarAsync<string>(path, $"SELECT profile_id FROM {table};"));
        }
        Assert.Contains("example", await ScalarAsync<string>(path, "SELECT payload_json FROM run_audits;"), StringComparison.Ordinal);
        Assert.Contains("example", await ScalarAsync<string>(path, "SELECT payload_json FROM evaluation_audits;"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SEC_001_CredentialValuesCannotBeImportedOrPersistedOrEchoedByValidation()
    {
        const string secret = "synthetic-secret-value!";
        var (path, repository) = await CreateRepositoryAsync();
        using var source = LegacySource.Create(profile: LegacySource.ValidProfile.Replace(
            "TEST_ADO_PAT", secret, StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            new LegacyProfileImporter(repository).ImportAsync(source.ProfilePath));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)), StringComparison.Ordinal);
    }

    private async Task<(string Path, SqliteSingletonProfileRepository Repository)> CreateRepositoryAsync()
    {
        var path = DatabasePath();
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        return (path, new SqliteSingletonProfileRepository(path));
    }

    private string DatabasePath()
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
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
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class RecordingValidator : IDeploymentConfigurationValidator
    {
        private readonly DeploymentConfigurationValidator inner = new();
        public int ValidateCalls { get; private set; }
        public DeploymentConfiguration Validate(DeploymentProfileInput profileInput, IntakePolicyInput policyInput,
            string profileSource = "profile", string policySource = "intake policy")
        {
            ValidateCalls++;
            return inner.Validate(profileInput, policyInput, profileSource, policySource);
        }
        public DeploymentProfile ValidateProfile(DeploymentProfileInput input, string source = "profile") =>
            inner.ValidateProfile(input, source);
        public IntakePolicy ValidatePolicy(IntakePolicyInput input, string source = "intake policy") =>
            inner.ValidatePolicy(input, source);
    }

    private sealed class LegacySource : IDisposable
    {
        public const string ValidProfile = """
            profile:
              id: stable-profile
              version: "1"
            intakePolicy:
              path: intake-policy.yaml
              url: https://example.invalid/policy
            intakeState:
              validatedTag: VALIDATED
              incompleteTag: INCOMPLETE
            ado:
              organizationUrl: https://dev.azure.com/example
              project: Example
              savedQueryId: 11111111-1111-1111-1111-111111111111
              authentication:
                patEnvironmentVariable: TEST_ADO_PAT
            ai:
              provider: openai
              model: example-model
              timeoutSeconds: 60
              authentication:
                apiKeyEnvironmentVariable: TEST_AI_KEY
              pricing: []
            schedule:
              enabled: false
              expression: "0 * * * * *"
              timezone: UTC
              initialLookback: 1.00:00:00
            processing:
              executionMode: DRY_RUN
              concurrency: 1
              retries: 1
              contentLimits:
                maximumTotalCharacters: 1000
                maximumComments: 10
                maximumExtractedTextCharacters: 500
              attachmentLimits:
                maximumCount: 2
                maximumBytesPerAttachment: 1000
                maximumAggregateBytes: 2000
                maximumPdfPages: 10
            audit:
              retentionDays: 90
            exclusions: []
            """;

        private const string ValidPolicy = """
            policy:
              id: stable-policy
              version: "1"
            criteria:
              - id: context
                displayName: Context
                description: Relevant context.
                applicability: required
                na:
                  allowed: false
                  requiresExplanation: false
                evaluationGuidance: Determine whether context is present.
            """;

        private LegacySource(string path)
        {
            DirectoryPath = path;
            ProfilePath = Path.Combine(path, "profile.yaml");
            PolicyPath = Path.Combine(path, "intake-policy.yaml");
        }

        public string DirectoryPath { get; }
        public string ProfilePath { get; }
        public string PolicyPath { get; }

        public static LegacySource Create(string? profile = null, string? policy = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"intake-gate-legacy-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "profile.yaml"), profile ?? ValidProfile);
            File.WriteAllText(Path.Combine(path, "intake-policy.yaml"), policy ?? ValidPolicy);
            return new LegacySource(path);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}

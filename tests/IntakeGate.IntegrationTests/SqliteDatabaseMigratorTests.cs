using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using System.Reflection;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteDatabaseMigratorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-migrations-{Guid.NewGuid():N}");

    [Fact]
    public async Task DB_001_CFG_007_FreshDatabaseMigratesSequentiallyToCurrentSchema()
    {
        var path = Path.Combine(directory, "nested", "fresh.db");

        await new SqliteDatabaseMigrator(path).MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        var tables = await ReadTableNamesAsync(path);
        Assert.Contains("application_runtime_records", tables);
        Assert.Contains("run_audits", tables);
        Assert.Contains("evaluation_audits", tables);
        Assert.Contains("incremental_run_audits", tables);
        Assert.Contains("discovery_checkpoints", tables);
        Assert.Contains("discovered_work_registrations", tables);
        Assert.Contains("active_run_leases", tables);
        Assert.Contains("mutation_reconciliations", tables);
        Assert.Contains("singleton_profile_configuration", tables);
        Assert.Contains("local_users", tables);
        Assert.Contains("credential_slots", tables);
        Assert.Contains("setup_progress", tables);
        Assert.Contains("control_plane_audits", tables);
        Assert.Contains("ado_configuration_state", tables);
        Assert.Contains("ado_setup_settings", tables);
        Assert.Contains("ai_configuration_state", tables);
        Assert.Contains("ai_setup_settings", tables);
        Assert.Contains("runtime_configuration_generations", tables);
        Assert.Contains("active_runtime_configuration", tables);
        Assert.Contains("onboarding_profile_draft", tables);
        Assert.Equal(1L, await ScalarAsync<long>(path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_evaluation_audits_evaluated';"));
    }

    [Fact]
    public async Task DB_001_AC_17_CFG_008_Version5DataSurvivesPhase1AMigration()
    {
        var path = Path.Combine(directory, "upgrade.db");
        await CreateVersion5DatabaseAsync(path);

        await new SqliteDatabaseMigrator(path).MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        Assert.Equal("runtime-1", await ScalarAsync<string>(path, "SELECT id FROM application_runtime_records;"));
        Assert.Equal("run-1", await ScalarAsync<string>(path, "SELECT run_id FROM run_audits;"));
        Assert.Equal("evaluation-1", await ScalarAsync<string>(path, "SELECT evaluation_id FROM evaluation_audits;"));
        Assert.Equal("incremental-1", await ScalarAsync<string>(path, "SELECT run_id FROM incremental_run_audits;"));
        Assert.Equal(1L, await ScalarAsync<long>(path,
            "SELECT COUNT(*) FROM run_audits WHERE configuration_generation_id IS NULL;"));
        Assert.Equal(1L, await ScalarAsync<long>(path,
            "SELECT COUNT(*) FROM incremental_run_audits WHERE configuration_generation_id IS NULL;"));
        Assert.Equal("profile-stable", await ScalarAsync<string>(path, "SELECT profile_id FROM discovery_checkpoints;"));
        Assert.Equal(42L, await ScalarAsync<long>(path, "SELECT work_item_id FROM discovered_work_registrations;"));
        Assert.Equal("lease-run", await ScalarAsync<string>(path, "SELECT run_id FROM active_run_leases;"));
        Assert.Equal("evaluation-1", await ScalarAsync<string>(path, "SELECT evaluation_id FROM mutation_reconciliations;"));
        Assert.Equal(0L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM singleton_profile_configuration;"));
    }

    [Fact]
    public async Task DB_001_AUTH_001_Version6ProfileDataSurvivesAdditiveUserMigration()
    {
        var path = Path.Combine(directory, "phase1b-upgrade.db");
        await CreateVersion5DatabaseAsync(path);
        await ExecuteAsync(path, """
            CREATE TABLE singleton_profile_configuration (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                profile_id TEXT NOT NULL UNIQUE,
                profile_json TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, 'phase1b-profile', '{}', '{}', '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
            PRAGMA user_version = 6;
            """);

        await new SqliteDatabaseMigrator(path).MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        Assert.Equal("phase1b-profile", await ScalarAsync<string>(path,
            "SELECT profile_id FROM singleton_profile_configuration;"));
        Assert.Equal(0L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM local_users;"));
    }

    [Fact]
    public async Task DB_001_SEC_009_Schema7UpgradesInPlaceAndImportsOnlyLegacyReferenceNames()
    {
        var path = Path.Combine(directory, "phase2a-upgrade.db");
        await CreateVersion5DatabaseAsync(path);
        await ExecuteAsync(path, """
            CREATE TABLE singleton_profile_configuration (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                profile_id TEXT NOT NULL UNIQUE,
                profile_json TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE local_users (
                user_id TEXT NOT NULL PRIMARY KEY,
                username TEXT NOT NULL,
                normalized_username TEXT NOT NULL UNIQUE,
                display_name TEXT NULL,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL CHECK (role IN ('Admin', 'Viewer')),
                created_at_utc TEXT NOT NULL,
                password_changed_at_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ix_local_users_normalized_username ON local_users(normalized_username);
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, 'stable-profile',
                '{"ado":{"authentication":{"patEnvironmentVariable":"MIGRATED_ADO_REFERENCE"}},"ai":{"provider":"openai","authentication":{"apiKeyEnvironmentVariable":"MIGRATED_AI_REFERENCE"}}}',
                '{}', '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
            INSERT INTO local_users VALUES
                ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Admin', 'ADMIN', NULL, 'retained-hash',
                 'Admin', '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
            PRAGMA user_version = 7;
            """);

        await new SqliteDatabaseMigrator(path).MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        Assert.Equal("retained-hash", await ScalarAsync<string>(path, "SELECT password_hash FROM local_users;"));
        Assert.Equal("runtime-1", await ScalarAsync<string>(path, "SELECT id FROM application_runtime_records;"));
        Assert.Equal("MIGRATED_ADO_REFERENCE", await ScalarAsync<string>(path,
            "SELECT environment_variable_name FROM credential_slots WHERE slot = 'AzureDevOps';"));
        Assert.Equal("MIGRATED_AI_REFERENCE", await ScalarAsync<string>(path,
            "SELECT environment_variable_name FROM credential_slots WHERE slot = 'OpenAi';"));
        Assert.Equal(0L, await ScalarAsync<long>(path,
            "SELECT COUNT(*) FROM credential_slots WHERE ciphertext IS NOT NULL;"));
    }

    [Theory]
    [InlineData(8)]  // immediately before ADO/profile control-plane persistence
    [InlineData(10)] // immediately before immutable runtime generations
    [InlineData(11)] // runtime generations before onboarding draft persistence
    [InlineData(13)] // immediately previous schema
    public async Task DB_001_Phase7_StrategicSchemaUpgradeMatrixPreservesApplicableState(int sourceVersion)
    {
        var path = Path.Combine(directory, $"strategic-v{sourceVersion}.db");
        await CreateDatabaseAtVersionAsync(path, sourceVersion);
        await SeedStrategicStateAsync(path, sourceVersion);

        await new SqliteDatabaseMigrator(path).MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        Assert.Equal("upgrade-runtime", await ScalarAsync<string>(path,
            "SELECT id FROM application_runtime_records;"));
        Assert.Equal("upgrade-run", await ScalarAsync<string>(path,
            "SELECT run_id FROM run_audits;"));
        Assert.Equal("upgrade-evaluation", await ScalarAsync<string>(path,
            "SELECT evaluation_id FROM evaluation_audits;"));
        Assert.Equal("upgrade-profile", await ScalarAsync<string>(path,
            "SELECT profile_id FROM singleton_profile_configuration;"));
        Assert.Equal("retained-password-hash", await ScalarAsync<string>(path,
            "SELECT password_hash FROM local_users;"));
        Assert.Equal("UPGRADE_ADO_REFERENCE", await ScalarAsync<string>(path,
            "SELECT environment_variable_name FROM credential_slots WHERE slot = 'AzureDevOps';"));
        Assert.Equal("UpgradeAudit", await ScalarAsync<string>(path,
            "SELECT operation FROM control_plane_audits;"));

        if (sourceVersion >= 9)
            Assert.Equal("UpgradeProject", await ScalarAsync<string>(path,
                "SELECT project FROM ado_setup_settings;"));
        if (sourceVersion >= 10)
            Assert.Equal("upgrade-model", await ScalarAsync<string>(path,
                "SELECT model_id FROM ai_setup_settings;"));
        if (sourceVersion >= 11)
        {
            Assert.Equal(1L, await ScalarAsync<long>(path,
                "SELECT generation_id FROM active_runtime_configuration;"));
            Assert.Equal(1L, await ScalarAsync<long>(path,
                "SELECT configuration_generation_id FROM run_audits;"));
        }
        if (sourceVersion >= 12)
            Assert.Equal(3L, await ScalarAsync<long>(path,
                "SELECT revision FROM onboarding_profile_draft;"));
        if (sourceVersion >= 13)
        {
            Assert.Equal("ManualWorkItem", await ScalarAsync<string>(path,
                "SELECT trigger_type FROM run_audits;"));
            Assert.Equal("42", await ScalarAsync<string>(path,
                "SELECT work_item_id FROM evaluation_audits;"));
        }

        Assert.Equal(1L, await ScalarAsync<long>(path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_evaluation_audits_evaluated';"));
    }

    [Fact]
    public async Task DB_001_NFR_003_RepeatedMigrationDoesNotChangeVersionOrData()
    {
        var path = Path.Combine(directory, "repeat.db");
        var migrator = new SqliteDatabaseMigrator(path);
        await migrator.MigrateAsync();
        await ExecuteAsync(path, "INSERT INTO application_runtime_records VALUES ('runtime-1', 'instance-1', '2026-09-10T12:00:00Z', '1.0.0');");

        await migrator.MigrateAsync();

        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion, await ReadVersionAsync(path));
        Assert.Equal(1L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM application_runtime_records;"));
        Assert.Equal(1L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='singleton_profile_configuration';"));
    }

    [Fact]
    public async Task DB_001_MigrationFailureDoesNotAdvanceSchemaVersion()
    {
        var path = Path.Combine(directory, "failed.db");
        await CreateVersion5DatabaseAsync(path);
        await ExecuteAsync(path, "CREATE TABLE singleton_profile_configuration (wrong_column TEXT); ");

        await Assert.ThrowsAsync<SqliteException>(() => new SqliteDatabaseMigrator(path).MigrateAsync());

        Assert.Equal(SqliteDatabaseMigrator.PrePhase1ASchemaVersion, await ReadVersionAsync(path));
        Assert.Equal(1L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM pragma_table_info('singleton_profile_configuration') WHERE name='wrong_column';"));
    }

    [Fact]
    public async Task DB_001_UnsupportedFutureSchemaFailsWithoutDowngrade()
    {
        var path = Path.Combine(directory, "future.db");
        Directory.CreateDirectory(directory);
        await ExecuteAsync(path, $"PRAGMA user_version = {SqliteDatabaseMigrator.CurrentSchemaVersion + 1};");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqliteDatabaseMigrator(path).MigrateAsync());

        Assert.Contains("newer than supported", error.Message, StringComparison.Ordinal);
        Assert.Equal(SqliteDatabaseMigrator.CurrentSchemaVersion + 1, await ReadVersionAsync(path));
    }

    [Fact]
    public async Task CFG_008_SingletonProfileGroundworkRejectsASecondLogicalProfile()
    {
        var path = Path.Combine(directory, "singleton.db");
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        await ExecuteAsync(path, """
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, 'preserved-profile-id', '{}', '{}', '2026-09-10T12:00:00Z', '2026-09-10T12:00:00Z');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(path, """
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (2, 'second-profile', '{}', '{}', '2026-09-10T12:00:00Z', '2026-09-10T12:00:00Z');
            """));

        Assert.Equal("preserved-profile-id", await ScalarAsync<string>(path, "SELECT profile_id FROM singleton_profile_configuration;"));
    }

    private static async Task CreateVersion5DatabaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ExecuteAsync(path, """
            PRAGMA foreign_keys = ON;
            CREATE TABLE application_runtime_records (id TEXT NOT NULL PRIMARY KEY, instance_id TEXT NOT NULL, started_at_utc TEXT NOT NULL, application_version TEXT NOT NULL);
            CREATE TABLE run_audits (run_id TEXT NOT NULL PRIMARY KEY, started_at_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE evaluation_audits (evaluation_id TEXT NOT NULL PRIMARY KEY, run_id TEXT NOT NULL UNIQUE, evaluated_at_utc TEXT NOT NULL, payload_json TEXT NOT NULL, FOREIGN KEY(run_id) REFERENCES run_audits(run_id));
            CREATE INDEX ix_evaluation_audits_run_id ON evaluation_audits(run_id);
            CREATE TABLE incremental_run_audits (run_id TEXT NOT NULL PRIMARY KEY, started_at_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE discovery_checkpoints (profile_id TEXT NOT NULL PRIMARY KEY, advanced_at_utc TEXT NOT NULL, discovery_run_id TEXT NOT NULL);
            CREATE TABLE discovered_work_registrations (profile_id TEXT NOT NULL, work_item_id INTEGER NOT NULL, discovery_run_id TEXT NOT NULL, discovered_at_utc TEXT NOT NULL, selection_reason TEXT NOT NULL, processing_state TEXT NOT NULL, updated_at_utc TEXT NOT NULL, PRIMARY KEY (profile_id, work_item_id));
            CREATE INDEX ix_discovered_work_processable ON discovered_work_registrations(profile_id, processing_state, work_item_id);
            CREATE TABLE active_run_leases (profile_id TEXT NOT NULL PRIMARY KEY, run_id TEXT NOT NULL, acquired_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL);
            CREATE TABLE mutation_reconciliations (evaluation_id TEXT NOT NULL PRIMARY KEY, profile_id TEXT NOT NULL, work_item_id TEXT NOT NULL, status TEXT NOT NULL, updated_at_utc TEXT NOT NULL, payload_json TEXT NOT NULL, FOREIGN KEY(evaluation_id) REFERENCES evaluation_audits(evaluation_id));
            CREATE INDEX ix_mutation_reconciliations_pending ON mutation_reconciliations(profile_id, status, updated_at_utc);
            INSERT INTO application_runtime_records VALUES ('runtime-1', 'instance-1', '2026-09-10T12:00:00Z', '1.0.0');
            INSERT INTO run_audits (run_id, started_at_utc, payload_json)
            VALUES ('run-1', '2026-09-10T12:00:00Z', '{}');
            INSERT INTO evaluation_audits VALUES ('evaluation-1', 'run-1', '2026-09-10T12:00:01Z', '{}');
            INSERT INTO incremental_run_audits (run_id, started_at_utc, payload_json)
            VALUES ('incremental-1', '2026-09-10T12:00:00Z', '{}');
            INSERT INTO discovery_checkpoints VALUES ('profile-stable', '2026-09-10T12:00:00Z', 'discovery-1');
            INSERT INTO discovered_work_registrations VALUES ('profile-stable', 42, 'discovery-1', '2026-09-10T12:00:00Z', 'NewlyDiscovered', 'Pending', '2026-09-10T12:00:00Z');
            INSERT INTO active_run_leases VALUES ('profile-stable', 'lease-run', '2026-09-10T12:00:00Z', '2026-09-10T12:15:00Z');
            INSERT INTO mutation_reconciliations VALUES ('evaluation-1', 'profile-stable', '42', 'Pending', '2026-09-10T12:00:02Z', '{}');
            PRAGMA user_version = 5;
            """);
    }

    private static async Task CreateDatabaseAtVersionAsync(string path, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var field = typeof(SqliteDatabaseMigrator).GetField("Migrations", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Migration script catalog was not found.");
        var migrations = (IReadOnlyDictionary<int, string>?)field.GetValue(null)
            ?? throw new InvalidOperationException("Migration script catalog was unavailable.");
        for (var target = 1; target <= version; target++)
        {
            await ExecuteAsync(path, migrations[target]);
            await ExecuteAsync(path, $"PRAGMA user_version = {target};");
        }
    }

    private static async Task SeedStrategicStateAsync(string path, int version)
    {
        await ExecuteAsync(path, """
            INSERT INTO application_runtime_records VALUES
                ('upgrade-runtime', 'upgrade-instance', '2026-09-13T00:00:00Z', '0.9.0');
            INSERT INTO run_audits (run_id, started_at_utc, payload_json) VALUES
                ('upgrade-run', '2026-09-13T00:01:00Z', '{}');
            INSERT INTO evaluation_audits (evaluation_id, run_id, evaluated_at_utc, payload_json) VALUES
                ('upgrade-evaluation', 'upgrade-run', '2026-09-13T00:01:01Z', '{}');
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, 'upgrade-profile', '{}', '{}', '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z');
            INSERT INTO local_users VALUES
                ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Upgrade Admin', 'UPGRADE ADMIN', NULL,
                 'retained-password-hash', 'Admin', '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z');
            INSERT INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status)
            VALUES ('AzureDevOps', 'EnvironmentReference', 'UPGRADE_ADO_REFERENCE',
                    '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z', 'Verified');
            INSERT INTO control_plane_audits VALUES
                ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '2026-09-13T00:00:00Z',
                 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Upgrade Admin', 'UpgradeAudit',
                 'Profile', 'upgrade-profile', '["profileId"]');
            """);

        if (version >= 9)
        {
            await ExecuteAsync(path, """
                INSERT INTO ado_setup_settings
                    (singleton_id, organization_url, project, saved_query_id,
                     configuration_fingerprint, query_validated_at_utc, updated_at_utc, updated_by_user_id)
                VALUES (1, 'https://example.invalid/upgrade', 'UpgradeProject',
                        '11111111-1111-1111-1111-111111111111', 'sha256:upgrade-query',
                        '2026-09-13T00:02:00Z', '2026-09-13T00:02:00Z',
                        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                """);
        }
        if (version >= 10)
        {
            await ExecuteAsync(path, """
                INSERT INTO ai_setup_settings
                    (singleton_id, provider, model_id, credential_updated_at_utc,
                     model_validated_at_utc, updated_at_utc, updated_by_user_id)
                VALUES (1, 'openai', 'upgrade-model', '2026-09-13T00:00:00Z',
                        '2026-09-13T00:03:00Z', '2026-09-13T00:03:00Z',
                        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                """);
        }
        if (version >= 11)
        {
            await ExecuteAsync(path, """
                INSERT INTO runtime_configuration_generations
                    (generation_id, profile_id, profile_json, policy_json, configuration_fingerprint,
                     ado_credential_slot, ado_credential_source_kind, ado_credential_revision,
                     ai_credential_slot, ai_credential_source_kind, ai_credential_revision,
                     created_at_utc, created_by_user_id)
                VALUES (1, 'upgrade-profile', '{}', '{}', 'sha256:upgrade-generation',
                        'AzureDevOps', 'EnvironmentReference', '2026-09-13T00:00:00Z',
                        'OpenAi', 'EnvironmentReference', '2026-09-13T00:00:00Z',
                        '2026-09-13T00:04:00Z', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                INSERT INTO active_runtime_configuration VALUES
                    (1, 1, '2026-09-13T00:04:00Z');
                UPDATE run_audits SET configuration_generation_id = 1 WHERE run_id = 'upgrade-run';
                """);
        }
        if (version >= 12)
        {
            await ExecuteAsync(path, """
                INSERT INTO onboarding_profile_draft VALUES
                    (1, '{}', 3, '2026-09-13T00:05:00Z', '2026-09-13T00:05:00Z',
                     'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                """);
        }
        if (version >= 13)
        {
            await ExecuteAsync(path, """
                UPDATE run_audits SET parent_run_id = 'upgrade-parent', trigger_type = 'ManualWorkItem'
                    WHERE run_id = 'upgrade-run';
                UPDATE evaluation_audits SET parent_run_id = 'upgrade-parent', work_item_id = '42',
                    configuration_generation_id = 1 WHERE evaluation_id = 'upgrade-evaluation';
                """);
        }
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

    private static async Task<int> ReadVersionAsync(string path) =>
        Convert.ToInt32(await ScalarAsync<long>(path, "PRAGMA user_version;"));

    private static async Task<IReadOnlySet<string>> ReadTableNamesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

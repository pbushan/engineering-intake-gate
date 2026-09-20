using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

/// <summary>
/// The sole production owner of the application SQLite schema. Repositories assume this
/// migrator has completed successfully before they are used and never create or evolve tables.
/// </summary>
public sealed class SqliteDatabaseMigrator
{
    public const int CurrentSchemaVersion = 17;
    public const int PrePhase1ASchemaVersion = 5;

    private static readonly IReadOnlyDictionary<int, string> Migrations = new Dictionary<int, string>
    {
        [1] = """
            CREATE TABLE IF NOT EXISTS application_runtime_records (
                id TEXT NOT NULL PRIMARY KEY,
                instance_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                application_version TEXT NOT NULL
            );
            """,
        [2] = """
            CREATE TABLE IF NOT EXISTS run_audits (
                run_id TEXT NOT NULL PRIMARY KEY,
                started_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS evaluation_audits (
                evaluation_id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL UNIQUE,
                evaluated_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES run_audits(run_id)
            );
            CREATE INDEX IF NOT EXISTS ix_evaluation_audits_run_id ON evaluation_audits(run_id);
            """,
        [3] = """
            CREATE TABLE IF NOT EXISTS incremental_run_audits (
                run_id TEXT NOT NULL PRIMARY KEY,
                started_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS discovery_checkpoints (
                profile_id TEXT NOT NULL PRIMARY KEY,
                advanced_at_utc TEXT NOT NULL,
                discovery_run_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS discovered_work_registrations (
                profile_id TEXT NOT NULL,
                work_item_id INTEGER NOT NULL,
                discovery_run_id TEXT NOT NULL,
                discovered_at_utc TEXT NOT NULL,
                selection_reason TEXT NOT NULL,
                processing_state TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (profile_id, work_item_id)
            );
            CREATE INDEX IF NOT EXISTS ix_discovered_work_processable
                ON discovered_work_registrations(profile_id, processing_state, work_item_id);
            """,
        [4] = """
            CREATE TABLE IF NOT EXISTS active_run_leases (
                profile_id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL,
                acquired_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL
            );
            """,
        [5] = """
            CREATE TABLE IF NOT EXISTS mutation_reconciliations (
                evaluation_id TEXT NOT NULL PRIMARY KEY,
                profile_id TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                status TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(evaluation_id) REFERENCES evaluation_audits(evaluation_id)
            );
            CREATE INDEX IF NOT EXISTS ix_mutation_reconciliations_pending
                ON mutation_reconciliations(profile_id, status, updated_at_utc);
            """,
        [6] = """
            -- Reconcile the formerly fragmented version-5 bootstrap authorities. In particular,
            -- a version-5 database created through the audit repository may not yet have the
            -- runtime-registration table. These statements are additive and preserve all data.
            CREATE TABLE IF NOT EXISTS application_runtime_records (
                id TEXT NOT NULL PRIMARY KEY,
                instance_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                application_version TEXT NOT NULL
            );

            -- The constant primary key makes the zero-or-one profile deployment invariant a
            -- database constraint while persistence/import remains application-owned.
            CREATE TABLE singleton_profile_configuration (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                profile_id TEXT NOT NULL UNIQUE,
                profile_json TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """,
        [7] = """
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
            CREATE UNIQUE INDEX ix_local_users_normalized_username
                ON local_users(normalized_username);
            """,
        [8] = """
            CREATE TABLE credential_slots (
                slot TEXT NOT NULL PRIMARY KEY
                    CHECK (slot IN ('AzureDevOps', 'OpenAi', 'Anthropic')),
                source_kind TEXT NOT NULL
                    CHECK (source_kind IN ('LocallyEncrypted', 'EnvironmentReference')),
                ciphertext BLOB NULL,
                nonce BLOB NULL,
                authentication_tag BLOB NULL,
                environment_variable_name TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                verification_status TEXT NOT NULL
                    CHECK (verification_status IN ('NeverVerified', 'Verified', 'Failed')),
                last_verified_at_utc TEXT NULL,
                verification_diagnostic TEXT NULL,
                CHECK (
                    (source_kind = 'LocallyEncrypted' AND ciphertext IS NOT NULL AND nonce IS NOT NULL
                        AND authentication_tag IS NOT NULL AND environment_variable_name IS NULL)
                    OR
                    (source_kind = 'EnvironmentReference' AND ciphertext IS NULL AND nonce IS NULL
                        AND authentication_tag IS NULL AND environment_variable_name IS NOT NULL)
                )
            );

            -- Preserve the existing environment-reference configuration without ever resolving
            -- or persisting a process-environment value. Control-plane replacements own these
            -- rows; the active runtime remains unchanged until a new generation is activated.
            INSERT OR IGNORE INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status)
            SELECT 'AzureDevOps', 'EnvironmentReference',
                   json_extract(profile_json, '$.ado.authentication.patEnvironmentVariable'),
                   created_at_utc, updated_at_utc, 'NeverVerified'
            FROM singleton_profile_configuration
            WHERE json_extract(profile_json, '$.ado.authentication.patEnvironmentVariable') IS NOT NULL;

            INSERT OR IGNORE INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status)
            SELECT CASE lower(json_extract(profile_json, '$.ai.provider'))
                       WHEN 'openai' THEN 'OpenAi'
                       WHEN 'anthropic' THEN 'Anthropic'
                   END,
                   'EnvironmentReference',
                   json_extract(profile_json, '$.ai.authentication.apiKeyEnvironmentVariable'),
                   created_at_utc, updated_at_utc, 'NeverVerified'
            FROM singleton_profile_configuration
            WHERE lower(json_extract(profile_json, '$.ai.provider')) IN ('openai', 'anthropic')
              AND json_extract(profile_json, '$.ai.authentication.apiKeyEnvironmentVariable') IS NOT NULL;

            CREATE TABLE setup_progress (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                last_visited_step TEXT NOT NULL CHECK (last_visited_step IN
                    ('Welcome', 'BootstrapAdmin', 'AzureDevOps', 'Ai', 'SystemDefaults',
                     'Profile', 'SavedQuery', 'OptionalSchedule', 'ReviewFinish')),
                updated_at_utc TEXT NOT NULL,
                updated_by_user_id TEXT NOT NULL
            );

            CREATE TABLE control_plane_audits (
                audit_id TEXT NOT NULL PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                actor_user_id TEXT NOT NULL,
                actor_username TEXT NOT NULL,
                operation TEXT NOT NULL,
                target_category TEXT NOT NULL,
                target_id TEXT NOT NULL,
                changed_fields_json TEXT NOT NULL
            );
            CREATE INDEX ix_control_plane_audits_occurred_at
                ON control_plane_audits(occurred_at_utc, audit_id);
            """,
        [9] = """
            -- Store only the bounded authoritative ADO query boundary. Candidate
            -- previews/tokens and provider payloads are deliberately not persisted.
            CREATE TABLE ado_configuration_state (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                profile_id TEXT NOT NULL,
                configuration_generation INTEGER NOT NULL CHECK (configuration_generation > 0),
                configuration_fingerprint TEXT NOT NULL,
                saved_query_id TEXT NOT NULL,
                query_validated_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY(profile_id) REFERENCES singleton_profile_configuration(profile_id)
            );

            -- Profileless first-install staging for only the ADO organization/project step.
            -- It is not a profile, query authority, or runtime configuration.
            CREATE TABLE ado_setup_settings (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                organization_url TEXT NOT NULL,
                project TEXT NOT NULL,
                saved_query_id TEXT NULL,
                configuration_fingerprint TEXT NULL,
                query_validated_at_utc TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                updated_by_user_id TEXT NOT NULL
            );
            """,
        [10] = """
            -- Store only confirmed AI provider/model metadata and the credential
            -- revision against which the model was validated. Provider payloads and candidate
            -- tokens remain transient and are never persisted.
            CREATE TABLE ai_configuration_state (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                profile_id TEXT NOT NULL,
                provider TEXT NOT NULL CHECK (provider IN ('openai', 'anthropic')),
                model_id TEXT NOT NULL,
                credential_updated_at_utc TEXT NOT NULL,
                model_validated_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY(profile_id) REFERENCES singleton_profile_configuration(profile_id)
            );

            -- Profileless AI setup is a singleton staging record, not a profile or runtime
            -- authority. Profile creation consumes it while creating the one logical profile.
            CREATE TABLE ai_setup_settings (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                provider TEXT NOT NULL CHECK (provider IN ('openai', 'anthropic')),
                model_id TEXT NOT NULL,
                credential_updated_at_utc TEXT NOT NULL,
                model_validated_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                updated_by_user_id TEXT NOT NULL
            );
            """,
        [11] = """
            -- Runtime generations are immutable and secret-free. The singleton pointer is the
            -- only active generation; historical rows are append-only by contract.
            CREATE TABLE runtime_configuration_generations (
                generation_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                profile_json TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                configuration_fingerprint TEXT NOT NULL,
                ado_credential_slot TEXT NOT NULL,
                ado_credential_source_kind TEXT NOT NULL,
                ado_credential_revision TEXT NOT NULL,
                ai_credential_slot TEXT NOT NULL,
                ai_credential_source_kind TEXT NOT NULL,
                ai_credential_revision TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                created_by_user_id TEXT NOT NULL,
                UNIQUE(profile_id, configuration_fingerprint)
            );
            CREATE INDEX ix_runtime_configuration_generations_profile
                ON runtime_configuration_generations(profile_id, generation_id);
            CREATE TRIGGER runtime_configuration_generations_no_update
            BEFORE UPDATE ON runtime_configuration_generations
            BEGIN
                SELECT RAISE(ABORT, 'runtime configuration generations are immutable');
            END;
            CREATE TRIGGER runtime_configuration_generations_no_delete
            BEFORE DELETE ON runtime_configuration_generations
            BEGIN
                SELECT RAISE(ABORT, 'runtime configuration generations are immutable');
            END;

            CREATE TABLE active_runtime_configuration (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                generation_id INTEGER NOT NULL UNIQUE,
                activated_at_utc TEXT NOT NULL,
                FOREIGN KEY(generation_id) REFERENCES runtime_configuration_generations(generation_id)
            );

            ALTER TABLE run_audits ADD COLUMN configuration_generation_id INTEGER NULL
                REFERENCES runtime_configuration_generations(generation_id);
            ALTER TABLE incremental_run_audits ADD COLUMN configuration_generation_id INTEGER NULL
                REFERENCES runtime_configuration_generations(generation_id);
            """,
        [12] = """
            -- Singleton onboarding input. This row is temporary setup state only:
            -- it has no profile identity, runtime generation, query, provider, or credential fields.
            CREATE TABLE onboarding_profile_draft (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                draft_json TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                updated_by_user_id TEXT NOT NULL
            );
            """,
        [13] = """
            -- Additive operational read projections. Historical JSON remains the
            -- authority and old nullable values are not backfilled with invented metadata.
            ALTER TABLE run_audits ADD COLUMN parent_run_id TEXT NULL;
            ALTER TABLE run_audits ADD COLUMN trigger_type TEXT NULL;
            ALTER TABLE evaluation_audits ADD COLUMN parent_run_id TEXT NULL;
            ALTER TABLE evaluation_audits ADD COLUMN work_item_id TEXT NULL;
            ALTER TABLE evaluation_audits ADD COLUMN configuration_generation_id INTEGER NULL
                REFERENCES runtime_configuration_generations(generation_id);
            ALTER TABLE incremental_run_audits ADD COLUMN trigger_type TEXT NULL;
            ALTER TABLE incremental_run_audits ADD COLUMN status TEXT NULL;

            CREATE INDEX ix_run_audits_started_run
                ON run_audits(started_at_utc DESC, run_id DESC);
            CREATE INDEX ix_incremental_run_audits_started_run
                ON incremental_run_audits(started_at_utc DESC, run_id DESC);
            CREATE INDEX ix_evaluation_audits_parent
                ON evaluation_audits(parent_run_id, evaluated_at_utc, evaluation_id);
            CREATE INDEX ix_evaluation_audits_work_item
                ON evaluation_audits(work_item_id, evaluated_at_utc DESC);
            """,
        [14] = """
            -- Home aggregates are bounded by the authoritative evaluation UTC time.
            CREATE INDEX ix_evaluation_audits_evaluated
                ON evaluation_audits(evaluated_at_utc, evaluation_id);
            """,
        [15] = """
            -- Backend-owned model pricing cache. Decimal amounts are invariant strings so
            -- future cost accounting can retain exact values and historical provenance.
            -- ManualOverride is reserved for negotiated enterprise pricing and cannot be
            -- replaced by ordinary catalog refreshes.
            CREATE TABLE ai_model_pricing_cache (
                provider TEXT NOT NULL CHECK (provider IN ('openai', 'anthropic')),
                model_id TEXT NOT NULL,
                input_per_million_tokens TEXT NOT NULL,
                cached_input_per_million_tokens TEXT NULL,
                output_per_million_tokens TEXT NOT NULL,
                currency TEXT NOT NULL,
                source TEXT NOT NULL,
                source_uri TEXT NULL,
                catalog_version TEXT NOT NULL,
                effective_at_utc TEXT NULL,
                verified_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NULL,
                source_kind TEXT NOT NULL CHECK (source_kind IN
                    ('BundledCatalog', 'AuthoritativeCatalog', 'ManualOverride')),
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (provider, model_id)
            );
            """
        ,
        [16] = """
            -- Reusable evidence is intentionally separated from long-lived audit JSON so its
            -- shorter privacy retention can be enforced deterministically.
            CREATE TABLE attachment_evidence_artifacts (
                artifact_id TEXT NOT NULL PRIMARY KEY,
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX ix_attachment_evidence_scope_expiry
                ON attachment_evidence_artifacts(organization, project, expires_at_utc);

            CREATE TABLE analysis_context_snapshots (
                snapshot_id TEXT NOT NULL PRIMARY KEY,
                work_item_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX ix_analysis_context_expiry ON analysis_context_snapshots(expires_at_utc);

            CREATE TABLE reusable_evaluations (
                equivalence_key TEXT NOT NULL PRIMARY KEY,
                origin_evaluation_id TEXT NOT NULL,
                origin_run_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(origin_evaluation_id) REFERENCES evaluation_audits(evaluation_id)
                    ON DELETE CASCADE
            );
            CREATE INDEX ix_reusable_evaluations_expiry ON reusable_evaluations(expires_at_utc);

            -- Reserved for PR 2. Only selected key screenshots may be retained; sampled frames
            -- never enter this table.
            CREATE TABLE selected_key_screenshot_artifacts (
                screenshot_id TEXT NOT NULL PRIMARY KEY,
                source_artifact_id TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                storage_reference TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(source_artifact_id) REFERENCES attachment_evidence_artifacts(artifact_id)
                    ON DELETE CASCADE
            );
            CREATE INDEX ix_selected_screenshots_expiry
                ON selected_key_screenshot_artifacts(expires_at_utc);
            """,
        [17] = """
            -- Phase 2 activates the screenshot architecture reserved by schema 16. Bytes remain
            -- internal, bounded, scoped through the source artifact, and subject to the same TTL.
            ALTER TABLE selected_key_screenshot_artifacts ADD COLUMN media_type TEXT NULL;
            ALTER TABLE selected_key_screenshot_artifacts ADD COLUMN content_blob BLOB NULL;
            """
    };

    private readonly string connectionString;

    public SqliteDatabaseMigrator(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        var currentVersion = await ReadVersionAsync(connection, cancellationToken);
        if (currentVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        for (var targetVersion = currentVersion + 1; targetVersion <= CurrentSchemaVersion; targetVersion++)
        {
            await ApplyMigrationAsync(connection, targetVersion, Migrations[targetVersion], cancellationToken);
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int targetVersion,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var migration = connection.CreateCommand();
        migration.Transaction = (SqliteTransaction)transaction;
        migration.CommandText = sql;
        await migration.ExecuteNonQueryAsync(cancellationToken);

        await using var version = connection.CreateCommand();
        version.Transaction = (SqliteTransaction)transaction;
        version.CommandText = $"PRAGMA user_version = {targetVersion};";
        await version.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

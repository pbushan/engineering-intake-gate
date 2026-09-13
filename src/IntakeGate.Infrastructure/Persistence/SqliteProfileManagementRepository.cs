using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Profiles;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Setup;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteProfileManagementRepository : IProfileManagementRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string connectionString;
    private readonly IDeploymentConfigurationValidator validator;

    public SqliteProfileManagementRepository(string databasePath, IDeploymentConfigurationValidator validator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<ProfileConfigurationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var stored = await ReadAsync(connection, null, cancellationToken);
        return stored is null ? null : Snapshot(stored);
    }

    public async Task<ProfilePersistenceResult> CreateFromConfirmedSetupAsync(
        DeploymentConfiguration configuration,
        AzureDevOpsSetupSettings expectedAzureDevOps,
        AiConfigurationSettings expectedAi,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        var validated = Validate(configuration, "profile selected for creation");
        var serialized = Serialize(validated);
        var now = nowUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ProfileExistsAsync(connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.AlreadyExists);
        }
        if (!await ConfirmedSetupStillMatchesAsync(connection, transaction, validated,
                expectedAzureDevOps, expectedAi, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.SetupIncomplete);
        }

        await InsertProfileAsync(connection, transaction, validated, serialized, now, cancellationToken);
        var adoFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            validated.Profile.Identity.Id, validated.Profile.Ado);
        await using (var ado = connection.CreateCommand())
        {
            ado.Transaction = transaction;
            ado.CommandText = """
                INSERT INTO ado_configuration_state
                    (singleton_id, profile_id, configuration_generation, configuration_fingerprint,
                     saved_query_id, query_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, 1, $fingerprint, $queryId, $validatedAt, $now);
                """;
            ado.Parameters.AddWithValue("$profileId", validated.Profile.Identity.Id);
            ado.Parameters.AddWithValue("$fingerprint", adoFingerprint);
            ado.Parameters.AddWithValue("$queryId", validated.Profile.Ado.SavedQueryId.ToString("D"));
            ado.Parameters.AddWithValue("$validatedAt", FormatUtc(expectedAzureDevOps.QueryValidatedAtUtc!.Value));
            ado.Parameters.AddWithValue("$now", FormatUtc(now));
            await ado.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ai = connection.CreateCommand())
        {
            ai.Transaction = transaction;
            ai.CommandText = """
                INSERT INTO ai_configuration_state
                    (singleton_id, profile_id, provider, model_id, credential_updated_at_utc,
                     model_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, $provider, $model, $credential, $validatedAt, $now);
                """;
            ai.Parameters.AddWithValue("$profileId", validated.Profile.Identity.Id);
            ai.Parameters.AddWithValue("$provider", expectedAi.Provider!);
            ai.Parameters.AddWithValue("$model", expectedAi.Model!);
            ai.Parameters.AddWithValue("$credential", FormatUtc(expectedAi.CredentialUpdatedAtUtc!.Value));
            ai.Parameters.AddWithValue("$validatedAt", FormatUtc(expectedAi.ModelValidatedAtUtc!.Value));
            ai.Parameters.AddWithValue("$now", FormatUtc(now));
            await ai.ExecuteNonQueryAsync(cancellationToken);
        }
        await DeleteStagingAsync(connection, transaction, cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "ProfileCreated",
            validated.Profile.Identity.Id, ["profileId", "profileVersion", "policy", "schedule", "processing"],
            cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "ProfileSetupStagingConsumed",
            validated.Profile.Identity.Id, ["azureDevOpsSetup", "aiSetup", "credentialSlots"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ProfilePersistenceStatus.Committed,
            new ProfileConfigurationSnapshot(validated, Revision(serialized.ProfileJson, serialized.PolicyJson)));
    }

    public async Task<ProfilePersistenceResult> FinalizeFromOnboardingDraftAsync(
        DeploymentConfiguration configuration,
        AzureDevOpsSetupSettings expectedAzureDevOps,
        AiConfigurationSettings expectedAi,
        int expectedDraftRevision,
        RuntimeCredentialBinding azureDevOpsCredential,
        RuntimeCredentialBinding aiCredential,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        if (expectedDraftRevision <= 0) return new(ProfilePersistenceStatus.Conflict);
        var validated = Validate(configuration, "profile selected for onboarding finalization");
        if (validated.Profile.Processing.ExecutionMode == ExecutionMode.Live)
            return new(ProfilePersistenceStatus.Conflict);
        var serialized = Serialize(validated);
        var now = nowUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ProfileExistsAsync(connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.AlreadyExists);
        }
        if (!await AdminExistsAsync(connection, transaction, cancellationToken) ||
            !await DraftRevisionMatchesAsync(connection, transaction, expectedDraftRevision, cancellationToken) ||
            !await ConfirmedSetupStillMatchesAsync(connection, transaction, validated,
                expectedAzureDevOps, expectedAi, cancellationToken) ||
            !await CredentialBindingMatchesAsync(connection, transaction,
                azureDevOpsCredential, cancellationToken) ||
            !await CredentialBindingMatchesAsync(connection, transaction,
                aiCredential, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.SetupIncomplete);
        }

        await InsertProfileAsync(connection, transaction, validated, serialized, now, cancellationToken);
        var adoFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            validated.Profile.Identity.Id, validated.Profile.Ado);
        await using (var ado = connection.CreateCommand())
        {
            ado.Transaction = transaction;
            ado.CommandText = """
                INSERT INTO ado_configuration_state
                    (singleton_id, profile_id, configuration_generation, configuration_fingerprint,
                     saved_query_id, query_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, 1, $fingerprint, $queryId, $validatedAt, $now);
                """;
            ado.Parameters.AddWithValue("$profileId", validated.Profile.Identity.Id);
            ado.Parameters.AddWithValue("$fingerprint", adoFingerprint);
            ado.Parameters.AddWithValue("$queryId", validated.Profile.Ado.SavedQueryId.ToString("D"));
            ado.Parameters.AddWithValue("$validatedAt", FormatUtc(expectedAzureDevOps.QueryValidatedAtUtc!.Value));
            ado.Parameters.AddWithValue("$now", FormatUtc(now));
            await ado.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ai = connection.CreateCommand())
        {
            ai.Transaction = transaction;
            ai.CommandText = """
                INSERT INTO ai_configuration_state
                    (singleton_id, profile_id, provider, model_id, credential_updated_at_utc,
                     model_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, $provider, $model, $credential, $validatedAt, $now);
                """;
            ai.Parameters.AddWithValue("$profileId", validated.Profile.Identity.Id);
            ai.Parameters.AddWithValue("$provider", expectedAi.Provider!);
            ai.Parameters.AddWithValue("$model", expectedAi.Model!);
            ai.Parameters.AddWithValue("$credential", FormatUtc(expectedAi.CredentialUpdatedAtUtc!.Value));
            ai.Parameters.AddWithValue("$validatedAt", FormatUtc(expectedAi.ModelValidatedAtUtc!.Value));
            ai.Parameters.AddWithValue("$now", FormatUtc(now));
            await ai.ExecuteNonQueryAsync(cancellationToken);
        }

        var runtimeFingerprint = RuntimeConfigurationFingerprint.Create(validated,
            azureDevOpsCredential.Revision, aiCredential.Revision);
        long generationId;
        await using (var generation = connection.CreateCommand())
        {
            generation.Transaction = transaction;
            generation.CommandText = """
                INSERT INTO runtime_configuration_generations
                    (profile_id, profile_json, policy_json, configuration_fingerprint,
                     ado_credential_slot, ado_credential_source_kind, ado_credential_revision,
                     ai_credential_slot, ai_credential_source_kind, ai_credential_revision,
                     created_at_utc, created_by_user_id)
                VALUES ($profileId, $profile, $policy, $fingerprint,
                        $adoSlot, $adoSource, $adoRevision,
                        $aiSlot, $aiSource, $aiRevision, $created, $actor)
                RETURNING generation_id;
                """;
            generation.Parameters.AddWithValue("$profileId", validated.Profile.Identity.Id);
            generation.Parameters.AddWithValue("$profile", serialized.ProfileJson);
            generation.Parameters.AddWithValue("$policy", serialized.PolicyJson);
            generation.Parameters.AddWithValue("$fingerprint", runtimeFingerprint);
            BindCredential(generation, "ado", azureDevOpsCredential);
            BindCredential(generation, "ai", aiCredential);
            generation.Parameters.AddWithValue("$created", FormatUtc(now));
            generation.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
            generationId = Convert.ToInt64(await generation.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }
        await using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = """
                INSERT INTO active_runtime_configuration (singleton_id, generation_id, activated_at_utc)
                VALUES (1, $generation, $at);
                """;
            active.Parameters.AddWithValue("$generation", generationId);
            active.Parameters.AddWithValue("$at", FormatUtc(now));
            await active.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor,
                "RuntimeConfigurationGenerationCreated", "RuntimeConfiguration",
                generationId.ToString(CultureInfo.InvariantCulture),
                ["configurationFingerprint", "credentialRevisions", "onboardingFinalization"]),
            cancellationToken);
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor,
                "RuntimeConfigurationGenerationActivated", "RuntimeConfiguration",
                generationId.ToString(CultureInfo.InvariantCulture),
                ["activeGeneration", "configurationFingerprint"]), cancellationToken);
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                DELETE FROM onboarding_profile_draft WHERE singleton_id = 1 AND revision = $revision;
                DELETE FROM ado_setup_settings WHERE singleton_id = 1;
                DELETE FROM ai_setup_settings WHERE singleton_id = 1;
                """;
            consume.Parameters.AddWithValue("$revision", expectedDraftRevision);
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(connection, transaction, actor, now, "OnboardingSetupFinalized",
            validated.Profile.Identity.Id,
            ["profile", "policy", "azureDevOps", "ai", "schedule", "processing", "activeGeneration"],
            cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "OnboardingSetupStateConsumed",
            validated.Profile.Identity.Id,
            ["onboardingProfileDraft", "azureDevOpsSetup", "aiSetup"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var generationConfiguration = validated with { RuntimeGenerationId = generationId };
        var runtimeGeneration = new RuntimeConfigurationGeneration(generationId,
            validated.Profile.Identity.Id, generationConfiguration, runtimeFingerprint,
            azureDevOpsCredential, aiCredential, now);
        return new(ProfilePersistenceStatus.Committed,
            new ProfileConfigurationSnapshot(validated, Revision(serialized.ProfileJson, serialized.PolicyJson)),
            runtimeGeneration);
    }

    public async Task<ProfilePersistenceResult> ImportAsync(
        DeploymentConfiguration configuration,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        var validated = Validate(configuration, "profile selected for legacy import");
        var serialized = Serialize(validated);
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ProfileExistsAsync(connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.AlreadyExists);
        }
        await InsertProfileAsync(connection, transaction, validated, serialized, now, cancellationToken);
        await DeleteStagingAsync(connection, transaction, cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "ProfileLegacyImported",
            validated.Profile.Identity.Id,
            ["profileId", "profileVersion", "policy", "azureDevOps", "ai", "schedule", "processing"],
            cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "ProfileSetupStagingDiscarded",
            validated.Profile.Identity.Id, ["azureDevOpsSetup", "aiSetup"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ProfilePersistenceStatus.Committed,
            new ProfileConfigurationSnapshot(validated, Revision(serialized.ProfileJson, serialized.PolicyJson)));
    }

    public async Task<ProfilePersistenceResult> UpdateAsync(
        DeploymentConfiguration configuration,
        string expectedRevision,
        AuditActor actor,
        DateTimeOffset nowUtc,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        var validated = Validate(configuration, "profile selected for update");
        var serialized = Serialize(validated);
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.NotFound);
        }
        if (!FixedEquals(Revision(current.ProfileJson, current.PolicyJson), expectedRevision) ||
            !ProtectedConfigurationIsUnchanged(current.Configuration, validated))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProfilePersistenceStatus.Conflict);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE singleton_profile_configuration
                SET profile_json = $profile, policy_json = $policy, updated_at_utc = $now
                WHERE singleton_id = 1 AND profile_id = $profileId;
                """;
            update.Parameters.AddWithValue("$profile", serialized.ProfileJson);
            update.Parameters.AddWithValue("$policy", serialized.PolicyJson);
            update.Parameters.AddWithValue("$now", FormatUtc(now));
            update.Parameters.AddWithValue("$profileId", current.Configuration.Profile.Identity.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The singleton profile changed during an atomic profile update.");
        }
        await InsertAuditAsync(connection, transaction, actor, now, "ProfileUpdated",
            current.Configuration.Profile.Identity.Id, changedFields, cancellationToken);
        if (changedFields.Contains("schedule", StringComparer.Ordinal))
            await InsertAuditAsync(connection, transaction, actor, now, "ProfileScheduleChanged",
                current.Configuration.Profile.Identity.Id, ["schedule"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ProfilePersistenceStatus.Committed,
            new ProfileConfigurationSnapshot(validated, Revision(serialized.ProfileJson, serialized.PolicyJson)));
    }

    private DeploymentConfiguration Validate(DeploymentConfiguration configuration, string source) =>
        validator.ValidateConfiguration(configuration, source, "persisted SQLite policy");

    private static (string ProfileJson, string PolicyJson) Serialize(DeploymentConfiguration configuration) => (
        JsonSerializer.Serialize(DeploymentConfigurationDocuments.Profile(configuration.Profile), JsonOptions),
        JsonSerializer.Serialize(DeploymentConfigurationDocuments.Policy(configuration.Policy), JsonOptions));

    private async Task<StoredConfiguration?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT profile_id, profile_json, policy_json FROM singleton_profile_configuration WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var profileId = reader.GetString(0);
        var profileJson = reader.GetString(1);
        var policyJson = reader.GetString(2);
        var profile = JsonSerializer.Deserialize<DeploymentProfileInput>(profileJson, JsonOptions)
            ?? throw new ConfigurationDocumentException("The persisted profile document is empty.");
        var policy = JsonSerializer.Deserialize<IntakePolicyInput>(policyJson, JsonOptions)
            ?? throw new ConfigurationDocumentException("The persisted policy document is empty.");
        var configuration = validator.Validate(profile, policy, "persisted SQLite profile", "persisted SQLite policy");
        if (!string.Equals(profileId, configuration.Profile.Identity.Id, StringComparison.Ordinal))
            throw new ConfigurationDocumentException("The persisted singleton profile identity is inconsistent.");
        return new StoredConfiguration(configuration, profileJson, policyJson);
    }

    private static ProfileConfigurationSnapshot Snapshot(StoredConfiguration stored) =>
        new(stored.Configuration, Revision(stored.ProfileJson, stored.PolicyJson));

    private static async Task<bool> ConfirmedSetupStillMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeploymentConfiguration configuration,
        AzureDevOpsSetupSettings expectedAdo,
        AiConfigurationSettings expectedAi,
        CancellationToken cancellationToken)
    {
        await using (var ado = connection.CreateCommand())
        {
            ado.Transaction = transaction;
            ado.CommandText = """
                SELECT organization_url, project, saved_query_id, configuration_fingerprint,
                       query_validated_at_utc
                FROM ado_setup_settings WHERE singleton_id = 1;
                """;
            await using var reader = await ado.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) ||
                !string.Equals(reader.GetString(0), expectedAdo.OrganizationUrl.AbsoluteUri, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), expectedAdo.Project, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), configuration.Profile.Ado.SavedQueryId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reader.GetString(3), expectedAdo.ConfigurationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(4), FormatUtc(expectedAdo.QueryValidatedAtUtc!.Value), StringComparison.Ordinal))
                return false;
        }
        await using (var ai = connection.CreateCommand())
        {
            ai.Transaction = transaction;
            ai.CommandText = """
                SELECT provider, model_id, credential_updated_at_utc, model_validated_at_utc
                FROM ai_setup_settings WHERE singleton_id = 1;
                """;
            await using var reader = await ai.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), expectedAi.Provider, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), expectedAi.Model, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), FormatUtc(expectedAi.CredentialUpdatedAtUtc!.Value), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), FormatUtc(expectedAi.ModelValidatedAtUtc!.Value), StringComparison.Ordinal))
                return false;
        }
        return await CredentialIsCurrentAsync(connection, transaction, CredentialSlot.AzureDevOps,
                   null, expectedAdo.QueryValidatedAtUtc, cancellationToken) &&
               await CredentialIsCurrentAsync(connection, transaction,
                   AiProviderNames.CredentialSlot(expectedAi.Provider!), expectedAi.CredentialUpdatedAtUtc,
                   null, cancellationToken);
    }

    private static async Task<bool> CredentialIsCurrentAsync(
        SqliteConnection connection, SqliteTransaction transaction, CredentialSlot slot,
        DateTimeOffset? expectedUpdatedAtUtc, DateTimeOffset? maximumUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT updated_at_utc, verification_status FROM credential_slots WHERE slot = $slot;";
        command.Parameters.AddWithValue("$slot", slot.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               string.Equals(reader.GetString(1), "Verified", StringComparison.Ordinal) &&
               (expectedUpdatedAtUtc is null ||
                string.Equals(reader.GetString(0), FormatUtc(expectedUpdatedAtUtc.Value), StringComparison.Ordinal)) &&
               (maximumUpdatedAtUtc is null || ParseUtc(reader.GetString(0)) <= maximumUpdatedAtUtc.Value);
    }

    private static async Task InsertProfileAsync(
        SqliteConnection connection, SqliteTransaction transaction, DeploymentConfiguration configuration,
        (string ProfileJson, string PolicyJson) serialized, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            VALUES (1, $profileId, $profile, $policy, $now, $now);
            """;
        command.Parameters.AddWithValue("$profileId", configuration.Profile.Identity.Id);
        command.Parameters.AddWithValue("$profile", serialized.ProfileJson);
        command.Parameters.AddWithValue("$policy", serialized.PolicyJson);
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("The singleton profile constraint rejected a conflicting create.", exception);
        }
    }

    private static async Task<bool> ProfileExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM singleton_profile_configuration WHERE singleton_id = 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> AdminExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM local_users WHERE role = 'Admin');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> DraftRevisionMatchesAsync(
        SqliteConnection connection, SqliteTransaction transaction, int expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM onboarding_profile_draft WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull &&
               Convert.ToInt32(value, CultureInfo.InvariantCulture) == expectedRevision;
    }

    private static async Task<bool> CredentialBindingMatchesAsync(
        SqliteConnection connection, SqliteTransaction transaction, RuntimeCredentialBinding binding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_kind, updated_at_utc, verification_status
            FROM credential_slots WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue("$slot", binding.Slot.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               string.Equals(reader.GetString(0), binding.SourceKind.ToString(), StringComparison.Ordinal) &&
               string.Equals(reader.GetString(1), FormatUtc(binding.Revision), StringComparison.Ordinal) &&
               string.Equals(reader.GetString(2), "Verified", StringComparison.Ordinal);
    }

    private static void BindCredential(SqliteCommand command, string prefix, RuntimeCredentialBinding binding)
    {
        command.Parameters.AddWithValue($"${prefix}Slot", binding.Slot.ToString());
        command.Parameters.AddWithValue($"${prefix}Source", binding.SourceKind.ToString());
        command.Parameters.AddWithValue($"${prefix}Revision", FormatUtc(binding.Revision));
    }

    private static async Task DeleteStagingAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ado_setup_settings WHERE singleton_id = 1; DELETE FROM ai_setup_settings WHERE singleton_id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task InsertAuditAsync(
        SqliteConnection connection, SqliteTransaction transaction, AuditActor actor, DateTimeOffset at,
        string operation, string target, IReadOnlyList<string> fields, CancellationToken cancellationToken) =>
        SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), at, actor, operation, "Profile", target, fields),
            cancellationToken);

    private static bool ProtectedConfigurationIsUnchanged(
        DeploymentConfiguration current, DeploymentConfiguration candidate) =>
        string.Equals(current.Profile.Identity.Id, candidate.Profile.Identity.Id, StringComparison.Ordinal) &&
        string.Equals(current.Profile.IntakePolicy.Path, candidate.Profile.IntakePolicy.Path, StringComparison.Ordinal) &&
        current.Profile.Ado == candidate.Profile.Ado &&
        string.Equals(current.Profile.Ai.Provider, candidate.Profile.Ai.Provider, StringComparison.Ordinal) &&
        string.Equals(current.Profile.Ai.Model, candidate.Profile.Ai.Model, StringComparison.Ordinal) &&
        current.Profile.Ai.Authentication == candidate.Profile.Ai.Authentication;

    private static string Revision(string profileJson, string policyJson)
    {
        var bytes = Encoding.UTF8.GetBytes(profileJson + "\n" + policyJson);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static void ValidateActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }

    private sealed record StoredConfiguration(
        DeploymentConfiguration Configuration,
        string ProfileJson,
        string PolicyJson);
}

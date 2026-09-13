using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteRuntimeConfigurationGenerationRepository(
    string databasePath,
    IDeploymentConfigurationValidator validator) : IRuntimeConfigurationGenerationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task<RuntimeConfigurationGeneration?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.generation_id, g.profile_id, g.profile_json, g.policy_json,
                   g.configuration_fingerprint, g.ado_credential_slot,
                   g.ado_credential_source_kind, g.ado_credential_revision,
                   g.ai_credential_slot, g.ai_credential_source_kind,
                   g.ai_credential_revision, g.created_at_utc
            FROM active_runtime_configuration a
            JOIN runtime_configuration_generations g ON g.generation_id = a.generation_id
            WHERE a.singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<RuntimeConfigurationGeneration?> GetAsync(
        long generationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation_id, profile_id, profile_json, policy_json,
                   configuration_fingerprint, ado_credential_slot,
                   ado_credential_source_kind, ado_credential_revision,
                   ai_credential_slot, ai_credential_source_kind,
                   ai_credential_revision, created_at_utc
            FROM runtime_configuration_generations WHERE generation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<RuntimeConfigurationGeneration> ActivateAsync(
        DeploymentConfiguration configuration,
        string configurationFingerprint,
        RuntimeCredentialBinding azureDevOpsCredential,
        RuntimeCredentialBinding aiCredential,
        AuditActor actor,
        DateTimeOffset nowUtc,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var expectedFingerprint = RuntimeConfigurationFingerprint.Create(configuration,
            azureDevOpsCredential.Revision, aiCredential.Revision);
        if (!string.Equals(expectedFingerprint, configurationFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The runtime configuration fingerprint is inconsistent.", nameof(configurationFingerprint));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await ExistingAsync(connection, transaction, configuration.Profile.Identity.Id,
            configurationFingerprint, cancellationToken);
        long generationId;
        if (existing is { } existingId)
        {
            generationId = existingId;
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
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
            insert.Parameters.AddWithValue("$profileId", configuration.Profile.Identity.Id);
            insert.Parameters.AddWithValue("$profile", JsonSerializer.Serialize(
                DeploymentConfigurationInputs.Profile(configuration.Profile), JsonOptions));
            insert.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(
                DeploymentConfigurationInputs.Policy(configuration.Policy), JsonOptions));
            insert.Parameters.AddWithValue("$fingerprint", configurationFingerprint);
            Bind(insert, "ado", azureDevOpsCredential);
            Bind(insert, "ai", aiCredential);
            insert.Parameters.AddWithValue("$created", Format(nowUtc));
            insert.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
            generationId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            await SqliteSecretStore.InsertAuditAsync(connection, transaction,
                NewAudit(actor, nowUtc, "RuntimeConfigurationGenerationCreated", generationId,
                    changedFields.Concat(["configurationFingerprint", "credentialRevisions"]).ToArray()), cancellationToken);
        }

        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                INSERT INTO active_runtime_configuration (singleton_id, generation_id, activated_at_utc)
                VALUES (1, $generation, $at)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    generation_id = excluded.generation_id, activated_at_utc = excluded.activated_at_utc;
                """;
            activate.Parameters.AddWithValue("$generation", generationId);
            activate.Parameters.AddWithValue("$at", Format(nowUtc));
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            NewAudit(actor, nowUtc, "RuntimeConfigurationGenerationActivated", generationId,
                ["activeGeneration", "configurationFingerprint"]), cancellationToken);
        if (changedFields.Any(field => field.Contains("schedule", StringComparison.OrdinalIgnoreCase)))
            await SqliteSecretStore.InsertAuditAsync(connection, transaction,
                NewAudit(actor, nowUtc, "RuntimeSchedulerReconfigured", generationId, ["schedule"]),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(generationId, cancellationToken))!;
    }

    private RuntimeConfigurationGeneration Read(SqliteDataReader reader)
    {
        var profile = JsonSerializer.Deserialize<DeploymentProfileInput>(reader.GetString(2), JsonOptions)
            ?? throw new InvalidOperationException("Runtime generation profile is empty.");
        var policy = JsonSerializer.Deserialize<IntakePolicyInput>(reader.GetString(3), JsonOptions)
            ?? throw new InvalidOperationException("Runtime generation policy is empty.");
        var configuration = validator.Validate(profile, policy,
            "immutable runtime generation profile", "immutable runtime generation policy") with
        { RuntimeGenerationId = reader.GetInt64(0) };
        if (!string.Equals(configuration.Profile.Identity.Id, reader.GetString(1), StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime generation profile identity is inconsistent.");
        var adoCredential = new RuntimeCredentialBinding(Enum.Parse<CredentialSlot>(reader.GetString(5)),
            Enum.Parse<SecretSourceKind>(reader.GetString(6)), Parse(reader.GetString(7)));
        var aiCredential = new RuntimeCredentialBinding(Enum.Parse<CredentialSlot>(reader.GetString(8)),
            Enum.Parse<SecretSourceKind>(reader.GetString(9)), Parse(reader.GetString(10)));
        var fingerprint = reader.GetString(4);
        if (!string.Equals(fingerprint, RuntimeConfigurationFingerprint.Create(
                configuration, adoCredential.Revision, aiCredential.Revision), StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime generation fingerprint is inconsistent.");
        return new RuntimeConfigurationGeneration(
            reader.GetInt64(0), reader.GetString(1), configuration, fingerprint,
            adoCredential, aiCredential,
            Parse(reader.GetString(11)));
    }

    private static async Task<long?> ExistingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string profileId, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation_id FROM runtime_configuration_generations WHERE profile_id = $profile AND configuration_fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void Bind(SqliteCommand command, string prefix, RuntimeCredentialBinding binding)
    {
        command.Parameters.AddWithValue($"${prefix}Slot", binding.Slot.ToString());
        command.Parameters.AddWithValue($"${prefix}Source", binding.SourceKind.ToString());
        command.Parameters.AddWithValue($"${prefix}Revision", Format(binding.Revision));
    }

    private static ControlPlaneAuditRecord NewAudit(AuditActor actor, DateTimeOffset at,
        string operation, long generation, IReadOnlyList<string> fields) =>
        new(Guid.NewGuid(), at.ToUniversalTime(), actor, operation, "RuntimeConfiguration",
            generation.ToString(CultureInfo.InvariantCulture), fields.Distinct(StringComparer.Ordinal).Order().ToArray());

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

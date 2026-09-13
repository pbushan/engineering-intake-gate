using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAiConfigurationRepository : IAiConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string connectionString;
    private readonly IDeploymentConfigurationValidator validator;

    public SqliteAiConfigurationRepository(string databasePath, IDeploymentConfigurationValidator validator)
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

    public async Task<AiConfigurationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var profile = await ReadConfigurationAsync(connection, null, cancellationToken);
        if (profile is not null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT provider, model_id, credential_updated_at_utc, model_validated_at_utc
                FROM ai_configuration_state WHERE singleton_id = 1 AND profile_id = $profileId;
                """;
            command.Parameters.AddWithValue("$profileId", profile.Profile.Identity.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var confirmed = await reader.ReadAsync(cancellationToken) &&
                string.Equals(reader.GetString(0), profile.Profile.Ai.Provider, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(1), profile.Profile.Ai.Model, StringComparison.Ordinal);
            return new AiConfigurationSettings(true, profile.Profile.Identity.Id,
                profile.Profile.Ai.Provider, profile.Profile.Ai.Model, confirmed,
                confirmed ? ParseUtc(reader.GetString(3)) : null,
                confirmed ? ParseUtc(reader.GetString(2)) : null,
                AiConfigurationFingerprint.Create(true, profile.Profile.Identity.Id,
                    profile.Profile.Ai.Provider, profile.Profile.Ai.Model));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT provider, model_id, credential_updated_at_utc, model_validated_at_utc
                FROM ai_setup_settings WHERE singleton_id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var provider = reader.GetString(0);
                var model = reader.GetString(1);
                return new AiConfigurationSettings(false, null, provider, model, true,
                    ParseUtc(reader.GetString(3)), ParseUtc(reader.GetString(2)),
                    AiConfigurationFingerprint.Create(false, null, provider, model));
            }
        }
        return new AiConfigurationSettings(false, null, null, null, false, null, null,
            AiConfigurationFingerprint.Create(false, null, null, null));
    }

    public async Task<AiConfigurationCommitResult> CommitValidatedAsync(
        DeploymentConfiguration? candidateConfiguration,
        string provider,
        string model,
        string expectedCurrentFingerprint,
        DateTimeOffset credentialUpdatedAtUtc,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        if (!AiProviderNames.TryNormalize(provider, out var normalizedProvider))
            throw new ArgumentException("Unsupported AI provider.", nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentFingerprint);
        var now = validatedAtUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadConfigurationAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        if ((current is null) != (candidateConfiguration is null))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AiConfigurationCommitStatus.ProfileStateChanged, current is not null, false);
        }
        var currentFingerprint = current is null
            ? await ReadProfilelessFingerprintAsync(connection, (SqliteTransaction)transaction, cancellationToken)
            : AiConfigurationFingerprint.Create(true, current.Profile.Identity.Id,
                current.Profile.Ai.Provider, current.Profile.Ai.Model);
        if (!string.Equals(currentFingerprint, expectedCurrentFingerprint, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AiConfigurationCommitStatus.Conflict, current is not null, false);
        }
        if (!await CredentialRevisionIsCurrentAsync(connection, (SqliteTransaction)transaction,
                normalizedProvider, credentialUpdatedAtUtc, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AiConfigurationCommitStatus.Conflict, current is not null, false);
        }

        if (current is null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ai_setup_settings
                    (singleton_id, provider, model_id, credential_updated_at_utc,
                     model_validated_at_utc, updated_at_utc, updated_by_user_id)
                VALUES (1, $provider, $model, $credential, $validated, $now, $actor)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    provider = excluded.provider, model_id = excluded.model_id,
                    credential_updated_at_utc = excluded.credential_updated_at_utc,
                    model_validated_at_utc = excluded.model_validated_at_utc,
                    updated_at_utc = excluded.updated_at_utc,
                    updated_by_user_id = excluded.updated_by_user_id;
                """;
            BindState(command, normalizedProvider, model, credentialUpdatedAtUtc, now, actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(connection, transaction, actor, now,
                "AiSetupConfigurationConfirmed", $"{normalizedProvider}:{model}",
                ["provider", "model", "credentialRevision", "profilelessSetup"], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AiConfigurationCommitStatus.Committed, false, false);
        }

        var validated = validator.ValidateConfiguration(candidateConfiguration!,
            "confirmed AI configuration", "persisted SQLite policy");
        if (!string.Equals(validated.Profile.Ai.Provider, normalizedProvider, StringComparison.Ordinal) ||
            !string.Equals(validated.Profile.Ai.Model, model, StringComparison.Ordinal) ||
            !NonAiEquivalent(current, validated))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AiConfigurationCommitStatus.Conflict, true, false);
        }
        var changed = !string.Equals(current.Profile.Ai.Provider, normalizedProvider, StringComparison.Ordinal) ||
                      !string.Equals(current.Profile.Ai.Model, model, StringComparison.Ordinal);
        if (changed)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE singleton_profile_configuration
                SET profile_json = $profile, updated_at_utc = $now
                WHERE singleton_id = 1 AND profile_id = $profileId;
                """;
            update.Parameters.AddWithValue("$profile", JsonSerializer.Serialize(
                DeploymentConfigurationInputs.Profile(validated.Profile), JsonOptions));
            update.Parameters.AddWithValue("$now", FormatUtc(now));
            update.Parameters.AddWithValue("$profileId", current.Profile.Identity.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The singleton profile changed during AI confirmation.");
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                INSERT INTO ai_configuration_state
                    (singleton_id, profile_id, provider, model_id, credential_updated_at_utc,
                     model_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, $provider, $model, $credential, $validated, $now)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    profile_id = excluded.profile_id, provider = excluded.provider,
                    model_id = excluded.model_id,
                    credential_updated_at_utc = excluded.credential_updated_at_utc,
                    model_validated_at_utc = excluded.model_validated_at_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            BindState(state, normalizedProvider, model, credentialUpdatedAtUtc, now, actor);
            state.Parameters.AddWithValue("$profileId", current.Profile.Identity.Id);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM ai_setup_settings WHERE singleton_id = 1;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        var fields = new List<string> { "modelValidation", "credentialRevision" };
        if (!string.Equals(current.Profile.Ai.Provider, normalizedProvider, StringComparison.Ordinal)) fields.Add("provider");
        if (!string.Equals(current.Profile.Ai.Model, model, StringComparison.Ordinal)) fields.Add("model");
        await InsertAuditAsync(connection, transaction, actor, now,
            changed ? "AiConfigurationChanged" : "AiConfigurationRevalidated",
            $"{normalizedProvider}:{model}", fields, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(changed ? AiConfigurationCommitStatus.Committed : AiConfigurationCommitStatus.AlreadyCurrent,
            true, false);
    }

    private async Task<DeploymentConfiguration?> ReadConfigurationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT profile_id, profile_json, policy_json FROM singleton_profile_configuration WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var profileId = reader.GetString(0);
        var profile = JsonSerializer.Deserialize<DeploymentProfileInput>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidOperationException("Persisted profile is empty.");
        var policy = JsonSerializer.Deserialize<IntakePolicyInput>(reader.GetString(2), JsonOptions)
            ?? throw new InvalidOperationException("Persisted policy is empty.");
        var configuration = validator.Validate(profile, policy, "persisted SQLite profile", "persisted SQLite policy");
        if (!string.Equals(profileId, configuration.Profile.Identity.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted singleton profile identity is inconsistent.");
        return configuration;
    }

    private static async Task<string> ReadProfilelessFingerprintAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT provider, model_id FROM ai_setup_settings WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? AiConfigurationFingerprint.Create(false, null, reader.GetString(0), reader.GetString(1))
            : AiConfigurationFingerprint.Create(false, null, null, null);
    }

    private static async Task<bool> CredentialRevisionIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string provider,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT updated_at_utc, verification_status
            FROM credential_slots WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue("$slot",
            AiProviderNames.CredentialSlot(provider).ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               string.Equals(reader.GetString(0), FormatUtc(expectedUpdatedAtUtc), StringComparison.Ordinal) &&
               string.Equals(reader.GetString(1), "Verified", StringComparison.Ordinal);
    }

    private static void BindState(
        SqliteCommand command, string provider, string model, DateTimeOffset credential,
        DateTimeOffset now, AuditActor actor)
    {
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$credential", FormatUtc(credential));
        command.Parameters.AddWithValue("$validated", FormatUtc(now));
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
    }

    private static bool NonAiEquivalent(DeploymentConfiguration current, DeploymentConfiguration candidate)
    {
        if (!string.Equals(current.PolicyFingerprint, candidate.PolicyFingerprint, StringComparison.Ordinal)) return false;
        var candidateWithoutAi = candidate.Profile with { Ai = current.Profile.Ai };
        return JsonSerializer.Serialize(DeploymentConfigurationInputs.Profile(current.Profile), JsonOptions) ==
               JsonSerializer.Serialize(DeploymentConfigurationInputs.Profile(candidateWithoutAi), JsonOptions);
    }

    private static Task InsertAuditAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction, AuditActor actor,
        DateTimeOffset at, string operation, string target, IReadOnlyList<string> fields,
        CancellationToken cancellationToken) => SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), at, actor, operation, "AiManagement", target, fields),
            cancellationToken);

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
}

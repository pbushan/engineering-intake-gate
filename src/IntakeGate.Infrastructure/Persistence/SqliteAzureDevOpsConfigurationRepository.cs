using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAzureDevOpsConfigurationRepository : IAzureDevOpsConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string connectionString;
    private readonly IDeploymentConfigurationValidator validator;

    public SqliteAzureDevOpsConfigurationRepository(
        string databasePath,
        IDeploymentConfigurationValidator validator)
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

    public async Task<AzureDevOpsConfigurationState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var current = await ReadConfigurationAsync(connection, null, cancellationToken);
        if (current is null) return null;
        var fingerprint = AzureDevOpsConfigurationFingerprint.Create(
            current.Profile.Identity.Id, current.Profile.Ado);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT configuration_generation, configuration_fingerprint, saved_query_id,
                   query_validated_at_utc
            FROM ado_configuration_state WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new AzureDevOpsConfigurationState(current.Profile.Identity.Id, 0, fingerprint, false, null);
        var persistedFingerprint = reader.GetString(1);
        var confirmed = string.Equals(persistedFingerprint, fingerprint, StringComparison.Ordinal) &&
                        Guid.TryParse(reader.GetString(2), out var queryId) &&
                        queryId == current.Profile.Ado.SavedQueryId;
        return new AzureDevOpsConfigurationState(current.Profile.Identity.Id, reader.GetInt32(0),
            fingerprint, confirmed,
            confirmed ? ParseUtc(reader.GetString(3)) : null);
    }

    public async Task<AzureDevOpsConfigurationCommitResult> CommitValidatedAsync(
        DeploymentConfiguration candidate,
        string expectedCurrentFingerprint,
        string candidateFingerprint,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFingerprint);
        ValidateActor(actor);
        var validated = validator.ValidateConfiguration(candidate,
            "confirmed Azure DevOps configuration", "persisted SQLite policy");
        var computedCandidateFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            validated.Profile.Identity.Id, validated.Profile.Ado);
        if (!string.Equals(computedCandidateFingerprint, candidateFingerprint, StringComparison.Ordinal))
            return new(AzureDevOpsConfigurationCommitStatus.Conflict, 0, false);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadConfigurationAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AzureDevOpsConfigurationCommitStatus.ProfileNotConfigured, 0, false);
        }
        var currentFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            current.Profile.Identity.Id, current.Profile.Ado);
        if (!string.Equals(currentFingerprint, expectedCurrentFingerprint, StringComparison.Ordinal) ||
            !NonAdoEquivalent(current, validated))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AzureDevOpsConfigurationCommitStatus.Conflict, 0, false);
        }

        var now = validatedAtUtc.ToUniversalTime();
        var priorGeneration = await ReadGenerationAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        var changed = !string.Equals(currentFingerprint, candidateFingerprint, StringComparison.Ordinal);
        var generation = priorGeneration == 0 ? 1 : changed ? checked(priorGeneration + 1) : priorGeneration;
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
                throw new InvalidOperationException("The singleton profile changed during ADO confirmation.");

            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                "DELETE FROM discovery_checkpoints WHERE profile_id = $profileId;",
                current.Profile.Identity.Id, cancellationToken);
            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                "DELETE FROM discovered_work_registrations WHERE profile_id = $profileId;",
                current.Profile.Identity.Id, cancellationToken);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                INSERT INTO ado_configuration_state
                    (singleton_id, profile_id, configuration_generation, configuration_fingerprint,
                     saved_query_id, query_validated_at_utc, updated_at_utc)
                VALUES (1, $profileId, $generation, $fingerprint, $queryId, $validatedAt, $now)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    profile_id = excluded.profile_id,
                    configuration_generation = excluded.configuration_generation,
                    configuration_fingerprint = excluded.configuration_fingerprint,
                    saved_query_id = excluded.saved_query_id,
                    query_validated_at_utc = excluded.query_validated_at_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            state.Parameters.AddWithValue("$profileId", current.Profile.Identity.Id);
            state.Parameters.AddWithValue("$generation", generation);
            state.Parameters.AddWithValue("$fingerprint", candidateFingerprint);
            state.Parameters.AddWithValue("$queryId", validated.Profile.Ado.SavedQueryId.ToString("D"));
            state.Parameters.AddWithValue("$validatedAt", FormatUtc(now));
            state.Parameters.AddWithValue("$now", FormatUtc(now));
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clearSetup = connection.CreateCommand())
        {
            clearSetup.Transaction = (SqliteTransaction)transaction;
            clearSetup.CommandText = "DELETE FROM ado_setup_settings WHERE singleton_id = 1;";
            await clearSetup.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            NewAudit(actor, now, "AzureDevOpsSavedQueryCandidateConfirmed", candidateFingerprint,
                ["organizationUrl", "project", "savedQueryId", "configurationGeneration"]), cancellationToken);
        if (changed)
        {
            var changedSettings = new List<string>();
            if (current.Profile.Ado.OrganizationUrl != validated.Profile.Ado.OrganizationUrl) changedSettings.Add("organizationUrl");
            if (!string.Equals(current.Profile.Ado.Project, validated.Profile.Ado.Project, StringComparison.Ordinal)) changedSettings.Add("project");
            if (current.Profile.Ado.SavedQueryId != validated.Profile.Ado.SavedQueryId) changedSettings.Add("savedQueryId");
            changedSettings.Add("configurationGeneration");
            await SqliteSecretStore.InsertAuditAsync(connection, transaction,
                NewAudit(actor, now, "AzureDevOpsConfigurationChanged", candidateFingerprint, changedSettings), cancellationToken);
            await SqliteSecretStore.InsertAuditAsync(connection, transaction,
                NewAudit(actor, now, "AzureDevOpsDiscoveryRebaselined", candidateFingerprint,
                    ["discoveryCheckpoint", "discoveryRegistrations"]), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(changed ? AzureDevOpsConfigurationCommitStatus.Committed : AzureDevOpsConfigurationCommitStatus.AlreadyCurrent,
            generation, false);
    }

    private async Task<DeploymentConfiguration?> ReadConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
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

    private static async Task<int> ReadGenerationAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT configuration_generation FROM ado_configuration_state WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$profileId", profileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool NonAdoEquivalent(DeploymentConfiguration current, DeploymentConfiguration candidate)
    {
        if (!string.Equals(current.PolicyFingerprint, candidate.PolicyFingerprint, StringComparison.Ordinal)) return false;
        var candidateWithoutAdo = candidate.Profile with { Ado = current.Profile.Ado };
        return JsonSerializer.Serialize(DeploymentConfigurationInputs.Profile(current.Profile), JsonOptions) ==
               JsonSerializer.Serialize(DeploymentConfigurationInputs.Profile(candidateWithoutAdo), JsonOptions);
    }

    private static ControlPlaneAuditRecord NewAudit(
        AuditActor actor, DateTimeOffset at, string operation, string target,
        IReadOnlyList<string> fields) => new(Guid.NewGuid(), at, actor, operation,
        "AzureDevOps", target, fields);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static void ValidateActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}

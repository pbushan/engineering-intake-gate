using System.Globalization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAzureDevOpsSetupRepository(string databasePath) : IAzureDevOpsSetupRepository
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task<AzureDevOpsSetupSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT organization_url, project, saved_query_id, configuration_fingerprint,
                   query_validated_at_utc, updated_at_utc
            FROM ado_setup_settings WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AzureDevOpsSetupSettings(new Uri(reader.GetString(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)))
            : null;
    }

    public async Task SaveAsync(
        Uri organizationUrl,
        string project,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationUrl);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO ado_setup_settings
                (singleton_id, organization_url, project, saved_query_id, configuration_fingerprint,
                 query_validated_at_utc, updated_at_utc, updated_by_user_id)
            VALUES (1, $organization, $project, NULL, NULL, NULL, $now, $actor)
            ON CONFLICT(singleton_id) DO UPDATE SET
                organization_url = excluded.organization_url,
                project = excluded.project,
                saved_query_id = NULL,
                configuration_fingerprint = NULL,
                query_validated_at_utc = NULL,
                updated_at_utc = excluded.updated_at_utc,
                updated_by_user_id = excluded.updated_by_user_id;
            """;
        command.Parameters.AddWithValue("$organization", organizationUrl.AbsoluteUri);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor, "AzureDevOpsSetupSettingsChanged",
                "AzureDevOps", "profileless", ["organizationUrl", "project"]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ConfirmQueryAsync(
        AzureDevOpsConfiguration configuration,
        string expectedCurrentFingerprint,
        string candidateFingerprint,
        AuditActor actor,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        var now = validatedAtUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        AzureDevOpsSetupSettings? current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT organization_url, project, saved_query_id FROM ado_setup_settings WHERE singleton_id = 1;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken)
                ? new AzureDevOpsSetupSettings(new Uri(reader.GetString(0)), reader.GetString(1),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), null, null, now)
                : null;
        }
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        var currentAdo = configuration with
        {
            OrganizationUrl = current.OrganizationUrl,
            Project = current.Project,
            SavedQueryId = current.SavedQueryId ?? Guid.Empty
        };
        if (!string.Equals(AzureDevOpsConfigurationFingerprint.Create("profileless", currentAdo),
                expectedCurrentFingerprint, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE ado_setup_settings
            SET organization_url = $organization, project = $project, saved_query_id = $query,
                configuration_fingerprint = $fingerprint, query_validated_at_utc = $validated,
                updated_at_utc = $validated, updated_by_user_id = $actor
            WHERE singleton_id = 1;
            """;
        update.Parameters.AddWithValue("$organization", configuration.OrganizationUrl.AbsoluteUri);
        update.Parameters.AddWithValue("$project", configuration.Project);
        update.Parameters.AddWithValue("$query", configuration.SavedQueryId.ToString("D"));
        update.Parameters.AddWithValue("$fingerprint", candidateFingerprint);
        update.Parameters.AddWithValue("$validated", now.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor, "AzureDevOpsSavedQueryCandidateConfirmed",
                "AzureDevOps", candidateFingerprint,
                ["organizationUrl", "project", "savedQueryId", "profilelessSetup"]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

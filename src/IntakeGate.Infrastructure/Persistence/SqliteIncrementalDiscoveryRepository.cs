using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Discovery;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

/// <summary>SQLite operational store for discovery, leases, and aggregate run audits.</summary>
public sealed class SqliteIncrementalDiscoveryRepository : IIncrementalDiscoveryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string connectionString;

    public SqliteIncrementalDiscoveryRepository(string databasePath)
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

    public async Task<DiscoveryCheckpoint?> GetCheckpointAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT advanced_at_utc, discovery_run_id FROM discovery_checkpoints WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new DiscoveryCheckpoint(profileId, ParseUtc(reader.GetString(0)), Guid.Parse(reader.GetString(1)));
    }

    public Task<IReadOnlyList<DiscoveredWorkRegistration>> GetRegistrationsAsync(string profileId, CancellationToken cancellationToken = default) =>
        ReadRegistrationsAsync(profileId, false, cancellationToken);

    public Task<IReadOnlyList<DiscoveredWorkRegistration>> GetProcessableRegistrationsAsync(string profileId, CancellationToken cancellationToken = default) =>
        ReadRegistrationsAsync(profileId, true, cancellationToken);

    public async Task RegisterAndAdvanceCheckpointAsync(string profileId, Guid discoveryRunId,
        IReadOnlyList<DiscoveryRegistrationRequest> selections, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await RegisterCoreAsync(profileId, null, discoveryRunId, selections, nowUtc, cancellationToken);

    public async Task RegisterAndAdvanceCheckpointForGenerationAsync(string profileId,
        long configurationGenerationId, Guid discoveryRunId,
        IReadOnlyList<DiscoveryRegistrationRequest> selections, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await RegisterCoreAsync(profileId, configurationGenerationId, discoveryRunId, selections, nowUtc, cancellationToken);

    private async Task RegisterCoreAsync(string profileId, long? configurationGenerationId, Guid discoveryRunId,
        IReadOnlyList<DiscoveryRegistrationRequest> selections, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var utc = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (configurationGenerationId is { } expected &&
            !await IsActiveGenerationAsync(connection, (SqliteTransaction)transaction, expected, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeConfigurationChangedDuringDiscoveryException();
        }
        foreach (var selection in selections.DistinctBy(selection => selection.WorkItemId))
        {
            await using var registration = connection.CreateCommand();
            registration.Transaction = (SqliteTransaction)transaction;
            registration.CommandText = """
                INSERT INTO discovered_work_registrations
                    (profile_id, work_item_id, discovery_run_id, discovered_at_utc, selection_reason, processing_state, updated_at_utc)
                VALUES ($profileId, $workItemId, $runId, $now, $reason, $state, $now)
                ON CONFLICT(profile_id, work_item_id) DO UPDATE SET
                    discovery_run_id = excluded.discovery_run_id,
                    selection_reason = excluded.selection_reason,
                    processing_state = $state,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            registration.Parameters.AddWithValue("$profileId", profileId);
            registration.Parameters.AddWithValue("$workItemId", selection.WorkItemId);
            registration.Parameters.AddWithValue("$runId", discoveryRunId.ToString("D"));
            registration.Parameters.AddWithValue("$now", FormatUtc(utc));
            registration.Parameters.AddWithValue("$reason", selection.Reason.ToString());
            registration.Parameters.AddWithValue("$state", selection.InitialState.ToString());
            await registration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = (SqliteTransaction)transaction;
        checkpoint.CommandText = """
            INSERT INTO discovery_checkpoints (profile_id, advanced_at_utc, discovery_run_id)
            VALUES ($profileId, $advancedAt, $runId)
            ON CONFLICT(profile_id) DO UPDATE SET
                advanced_at_utc = excluded.advanced_at_utc,
                discovery_run_id = excluded.discovery_run_id;
            """;
        checkpoint.Parameters.AddWithValue("$profileId", profileId);
        checkpoint.Parameters.AddWithValue("$advancedAt", FormatUtc(utc));
        checkpoint.Parameters.AddWithValue("$runId", discoveryRunId.ToString("D"));
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetProcessingStateAsync(string profileId, int workItemId, RegisteredWorkState state, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => await SetProcessingStateCoreAsync(profileId, null, workItemId, state, nowUtc, cancellationToken);

    public async Task SetProcessingStateForGenerationAsync(string profileId, long configurationGenerationId,
        int workItemId, RegisteredWorkState state, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
        => await SetProcessingStateCoreAsync(profileId, configurationGenerationId, workItemId, state, nowUtc, cancellationToken);

    private async Task SetProcessingStateCoreAsync(string profileId, long? configurationGenerationId,
        int workItemId, RegisteredWorkState state, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE discovered_work_registrations SET processing_state = $state, updated_at_utc = $now
            WHERE profile_id = $profileId AND work_item_id = $workItemId;
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$workItemId", workItemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return;
        if (configurationGenerationId is { } expected)
        {
            await using var current = connection.CreateCommand();
            current.CommandText = "SELECT generation_id FROM active_runtime_configuration WHERE singleton_id = 1;";
            var value = await current.ExecuteScalarAsync(cancellationToken);
            if (value is not null && Convert.ToInt64(value, CultureInfo.InvariantCulture) != expected) return;
        }
        throw new InvalidOperationException("Discovered work registration was not found.");
    }

    private static async Task<bool> IsActiveGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, long expected, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation_id FROM active_runtime_configuration WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && Convert.ToInt64(value, CultureInfo.InvariantCulture) == expected;
    }

    public async Task SaveRunAuditAsync(IncrementalRunAuditRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO incremental_run_audits
                (run_id, started_at_utc, payload_json, configuration_generation_id, trigger_type, status)
            VALUES ($runId, $startedAt, $payload, $generationId, $triggerType, $status)
            ON CONFLICT(run_id) DO UPDATE SET payload_json = excluded.payload_json,
                configuration_generation_id = excluded.configuration_generation_id,
                trigger_type = excluded.trigger_type,
                status = excluded.status;
            """;
        command.Parameters.AddWithValue("$runId", record.RunId.ToString("D"));
        command.Parameters.AddWithValue("$startedAt", FormatUtc(record.StartedAtUtc));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonOptions));
        command.Parameters.AddWithValue("$generationId", (object?)record.ConfigurationGenerationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$triggerType", record.TriggerType.ToString());
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IncrementalRunAuditRecord?> GetRunAuditAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM incremental_run_audits WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? null : JsonSerializer.Deserialize<IncrementalRunAuditRecord>(payload, JsonOptions);
    }

    public async Task<ActiveRunLease?> TryAcquireRunLeaseAsync(string profileId, Guid runId, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        var now = nowUtc.ToUniversalTime();
        var expires = now + leaseDuration;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The conflict predicate makes acquisition one SQLite write statement: a live holder can
        // never be replaced, while an expired process lease is safely recoverable after restart.
        command.CommandText = """
            INSERT INTO active_run_leases (profile_id, run_id, acquired_at_utc, expires_at_utc)
            VALUES ($profileId, $runId, $now, $expires)
            ON CONFLICT(profile_id) DO UPDATE SET
                run_id = excluded.run_id,
                acquired_at_utc = excluded.acquired_at_utc,
                expires_at_utc = excluded.expires_at_utc
            WHERE active_run_leases.expires_at_utc <= $now;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        command.Parameters.AddWithValue("$expires", FormatUtc(expires));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? new ActiveRunLease(runId, now, expires) : null;
    }

    public async Task<bool> RefreshRunLeaseAsync(string profileId, Guid runId, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE active_run_leases SET expires_at_utc = $expires WHERE profile_id = $profileId AND run_id = $runId;";
        command.Parameters.AddWithValue("$expires", FormatUtc(now + leaseDuration));
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseRunLeaseAsync(string profileId, Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM active_run_leases WHERE profile_id = $profileId AND run_id = $runId;";
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DiscoveredWorkRegistration>> ReadRegistrationsAsync(string profileId, bool processable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = processable
            ? "SELECT profile_id, work_item_id, discovery_run_id, discovered_at_utc, selection_reason, processing_state, updated_at_utc FROM discovered_work_registrations WHERE profile_id = $profileId AND processing_state IN ('Pending', 'Processing', 'Error') ORDER BY work_item_id;"
            : "SELECT profile_id, work_item_id, discovery_run_id, discovered_at_utc, selection_reason, processing_state, updated_at_utc FROM discovered_work_registrations WHERE profile_id = $profileId ORDER BY work_item_id;";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<DiscoveredWorkRegistration>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new DiscoveredWorkRegistration(reader.GetString(0), reader.GetInt32(1), Guid.Parse(reader.GetString(2)),
                ParseUtc(reader.GetString(3)), Enum.Parse<DiscoverySelectionReason>(reader.GetString(4)),
                Enum.Parse<RegisteredWorkState>(reader.GetString(5)), ParseUtc(reader.GetString(6))));
        return results;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static string FormatUtc(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

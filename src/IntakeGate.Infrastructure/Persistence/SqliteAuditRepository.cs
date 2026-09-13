using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAuditRepository : IAuditRepository, IReconciliationRepository, IControlPlaneAuditRepository, IControlPlaneAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;

    public SqliteAuditRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task SaveAsync(
        RunAuditRecord run,
        EvaluationAuditRecord evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (run.RunId != evaluation.RunId) throw new ArgumentException("Run and evaluation audit IDs must match.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var runCommand = connection.CreateCommand();
        runCommand.Transaction = (SqliteTransaction)transaction;
        runCommand.CommandText = """
            INSERT INTO run_audits
                (run_id, started_at_utc, payload_json, configuration_generation_id, parent_run_id, trigger_type)
            VALUES ($runId, $startedAtUtc, $payloadJson, $generationId, $parentRunId, $triggerType);
            """;
        runCommand.Parameters.AddWithValue("$runId", run.RunId.ToString("D"));
        runCommand.Parameters.AddWithValue("$startedAtUtc", run.StartedAtUtc.ToUniversalTime().ToString("O"));
        runCommand.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(run, JsonOptions));
        runCommand.Parameters.AddWithValue("$generationId", (object?)run.ConfigurationGenerationId ?? DBNull.Value);
        runCommand.Parameters.AddWithValue("$parentRunId", (object?)run.ParentRunId?.ToString("D") ?? DBNull.Value);
        runCommand.Parameters.AddWithValue("$triggerType", run.TriggerType.ToString());
        await runCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var evaluationCommand = connection.CreateCommand();
        evaluationCommand.Transaction = (SqliteTransaction)transaction;
        evaluationCommand.CommandText = """
            INSERT INTO evaluation_audits
                (evaluation_id, run_id, evaluated_at_utc, payload_json, parent_run_id, work_item_id,
                 configuration_generation_id)
            VALUES ($evaluationId, $runId, $evaluatedAtUtc, $payloadJson, $parentRunId, $workItemId,
                    $generationId);
            """;
        evaluationCommand.Parameters.AddWithValue("$evaluationId", evaluation.EvaluationId);
        evaluationCommand.Parameters.AddWithValue("$runId", evaluation.RunId.ToString("D"));
        evaluationCommand.Parameters.AddWithValue("$evaluatedAtUtc", evaluation.EvaluatedAtUtc.ToUniversalTime().ToString("O"));
        evaluationCommand.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(evaluation, JsonOptions));
        evaluationCommand.Parameters.AddWithValue("$parentRunId", (object?)evaluation.ParentRunId?.ToString("D") ?? DBNull.Value);
        evaluationCommand.Parameters.AddWithValue("$workItemId", evaluation.WorkItemId);
        evaluationCommand.Parameters.AddWithValue("$generationId", (object?)evaluation.ConfigurationGenerationId ?? DBNull.Value);
        await evaluationCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReadAsync<RunAuditRecord>("SELECT payload_json FROM run_audits WHERE run_id = $id;", runId.ToString("D"), cancellationToken);

    public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) =>
        ReadAsync<EvaluationAuditRecord>("SELECT payload_json FROM evaluation_audits WHERE evaluation_id = $id;", evaluationId, cancellationToken);

    public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReadAsync<EvaluationAuditRecord>("SELECT payload_json FROM evaluation_audits WHERE run_id = $id;", runId.ToString("D"), cancellationToken);

    public async Task<IReadOnlyList<ControlPlaneAuditRecord>> ListControlPlaneAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, occurred_at_utc, actor_user_id, actor_username, operation,
                   target_category, target_id, changed_fields_json
            FROM control_plane_audits
            ORDER BY occurred_at_utc, audit_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<ControlPlaneAuditRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ControlPlaneAuditRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                new AuditActor(Guid.Parse(reader.GetString(2)), reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? []));
        }

        return records;
    }

    public async Task<ControlPlaneAuditPage> ListControlPlaneAsync(
        ControlPlaneAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Page <= 0) throw new ArgumentOutOfRangeException(nameof(query.Page));
        if (query.PageSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(query.PageSize));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var (where, parameters) = BuildControlPlaneFilter(query);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM control_plane_audits{where};";
        Bind(countCommand, parameters);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT audit_id, occurred_at_utc, actor_user_id, actor_username, operation,
                   target_category, target_id, changed_fields_json
            FROM control_plane_audits{where}
            ORDER BY occurred_at_utc DESC, audit_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        Bind(pageCommand, parameters);
        pageCommand.Parameters.AddWithValue("$limit", query.PageSize);
        pageCommand.Parameters.AddWithValue("$offset", checked((query.Page - 1) * query.PageSize));

        var records = new List<ControlPlaneAuditRecord>();
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadControlPlane(reader));
        return new ControlPlaneAuditPage(query.Page, query.PageSize, totalCount, records);
    }

    public async Task AppendAsync(ControlPlaneAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Infrastructure.Secrets.SqliteSecretStore.InsertAuditAsync(
            connection, transaction, record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateMutationAuditAsync(
        string evaluationId,
        IReadOnlyList<ProposedMutation> attemptedMutations,
        IReadOnlyList<MutationOutcome> mutationOutcomes,
        MutationExecutionState mutationState,
        IReadOnlyList<string> errorCategories,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetEvaluationAsync(evaluationId, cancellationToken)
            ?? throw new InvalidOperationException("Evaluation audit was not found for mutation update.");
        var updated = existing with
        {
            AttemptedMutations = attemptedMutations,
            MutationOutcomes = mutationOutcomes,
            MutationState = mutationState,
            ErrorCategories = errorCategories.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            MutationAuditEvents = existing.MutationAuditEvents.Concat([
                new MutationAuditEvent(DateTimeOffset.UtcNow, attemptedMutations.ToArray(), mutationOutcomes.ToArray(), mutationState,
                    errorCategories.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())]).ToArray()
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE evaluation_audits SET payload_json = $payloadJson WHERE evaluation_id = $evaluationId;";
        command.Parameters.AddWithValue("$evaluationId", evaluationId);
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(updated, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Evaluation audit was not found for mutation update.");
    }

    public async Task UpdateSuppressionAuditAsync(
        string evaluationId,
        bool updateSuppressed,
        string? suppressionReason,
        bool materiallyChanged,
        CancellationToken cancellationToken = default)
    {
        if (updateSuppressed && string.IsNullOrWhiteSpace(suppressionReason))
            throw new ArgumentException("A suppression reason is required for a suppressed update.", nameof(suppressionReason));
        if (!updateSuppressed && suppressionReason is not null)
            throw new ArgumentException("A suppression reason is only valid for a suppressed update.", nameof(suppressionReason));

        var existing = await GetEvaluationAsync(evaluationId, cancellationToken)
            ?? throw new InvalidOperationException("Evaluation audit was not found for suppression update.");
        var updated = existing with
        {
            UpdateSuppressed = updateSuppressed,
            SuppressionReason = suppressionReason,
            MateriallyChanged = materiallyChanged
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE evaluation_audits SET payload_json = $payloadJson WHERE evaluation_id = $evaluationId;";
        command.Parameters.AddWithValue("$evaluationId", evaluationId);
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(updated, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Evaluation audit was not found for suppression update.");
    }

    public async Task<IReadOnlyList<EvaluationAuditRecord>> GetWorkItemHistoryAsync(
        string workItemId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Work-item/profile are retained inside the safe JSON audit payload.  This bounded
        // Operational history deliberately avoids introducing a generalized event store.
        command.CommandText = "SELECT payload_json FROM evaluation_audits ORDER BY evaluated_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<EvaluationAuditRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = JsonSerializer.Deserialize<EvaluationAuditRecord>(reader.GetString(0), JsonOptions);
            if (record is not null && string.Equals(record.WorkItemId, workItemId, StringComparison.Ordinal) &&
                string.Equals(record.ProfileId, profileId, StringComparison.Ordinal)) records.Add(record);
        }
        return records;
    }

    public async Task CreatePendingAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mutation_reconciliations (evaluation_id, profile_id, work_item_id, status, updated_at_utc, payload_json)
            VALUES ($evaluationId, $profileId, $workItemId, $status, $updatedAtUtc, $payload);
            """;
        BindReconciliation(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MutationReconciliationRecord>> GetPendingAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM mutation_reconciliations WHERE profile_id = $profileId AND status = $status ORDER BY updated_at_utc, evaluation_id;";
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$status", ReconciliationStatus.Pending.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<MutationReconciliationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = JsonSerializer.Deserialize<MutationReconciliationRecord>(reader.GetString(0), JsonOptions);
            if (record is not null) records.Add(record);
        }
        return records;
    }

    public async Task UpdateAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mutation_reconciliations SET status = $status, updated_at_utc = $updatedAtUtc, payload_json = $payload WHERE evaluation_id = $evaluationId;";
        BindReconciliation(command, record);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Mutation reconciliation was not found.");
    }

    private static void BindReconciliation(SqliteCommand command, MutationReconciliationRecord record)
    {
        command.Parameters.AddWithValue("$evaluationId", record.EvaluationId);
        command.Parameters.AddWithValue("$profileId", record.ProfileId);
        command.Parameters.AddWithValue("$workItemId", record.WorkItemId);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", record.UpdatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonOptions));
    }

    private static (string Where, IReadOnlyDictionary<string, object> Parameters) BuildControlPlaneFilter(
        ControlPlaneAuditQuery query)
    {
        var filters = new List<string>();
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
        if (query.OccurredFromUtc is { } from)
        {
            filters.Add("occurred_at_utc >= $from");
            parameters["$from"] = from.ToUniversalTime().ToString("O");
        }
        if (query.OccurredToUtc is { } to)
        {
            filters.Add("occurred_at_utc <= $to");
            parameters["$to"] = to.ToUniversalTime().ToString("O");
        }
        AddExact("actor_username", "$actor", query.Actor);
        AddExact("operation", "$operation", query.Operation);
        AddExact("target_category", "$category", query.TargetCategory);
        AddExact("target_id", "$target", query.TargetId);
        return (filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters), parameters);

        void AddExact(string column, string parameter, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            filters.Add($"{column} = {parameter} COLLATE NOCASE");
            parameters[parameter] = value.Trim();
        }
    }

    private static void Bind(SqliteCommand command, IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
    }

    private static ControlPlaneAuditRecord ReadControlPlane(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
        new AuditActor(Guid.Parse(reader.GetString(2)), reader.GetString(3)),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? []);

    private async Task<T?> ReadAsync<T>(string sql, string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

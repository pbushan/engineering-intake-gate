using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Discovery;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

/// <summary>Bounded SQL-backed operational history projection over the existing audit stores.</summary>
public sealed class SqliteOperationalAuditReader : IOperationalAuditReader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string connectionString;

    public SqliteOperationalAuditReader(string databasePath)
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

    public async Task<OperationalRunAuditPage> ListRunsAsync(
        OperationalRunQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Page <= 0) throw new ArgumentOutOfRangeException(nameof(query.Page));
        if (query.PageSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(query.PageSize));

        await using var connection = await OpenAsync(cancellationToken);
        var filter = BuildFilter(query);
        await using var count = connection.CreateCommand();
        count.CommandText = CommonTableExpression + " SELECT COUNT(*) FROM all_runs " + filter.Sql;
        BindFilter(count, query, filter);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        await using var page = connection.CreateCommand();
        page.CommandText = CommonTableExpression +
            " SELECT run_id, source, payload_json FROM all_runs" + filter.Sql +
            " ORDER BY started_at_utc DESC, run_id DESC LIMIT $limit OFFSET $offset;";
        BindFilter(page, query, filter);
        page.Parameters.AddWithValue("$limit", query.PageSize);
        page.Parameters.AddWithValue("$offset", checked((query.Page - 1) * query.PageSize));
        var rows = new List<(Guid RunId, string Source, string Payload)>();
        await using (var reader = await page.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }

        var evaluations = await ReadEvaluationsAsync(connection, rows.Select(row => row.RunId).ToArray(), cancellationToken);
        var envelopes = rows.Select(row => row.Source == "incremental"
                ? new OperationalRunAuditEnvelope(null,
                    Deserialize<IncrementalRunAuditRecord>(row.Payload),
                    evaluations.Where(item => item.ParentRunId == row.RunId).ToArray())
                : new OperationalRunAuditEnvelope(
                    Deserialize<RunAuditRecord>(row.Payload), null,
                    evaluations.Where(item => item.RunId == row.RunId || item.ParentRunId == row.RunId).ToArray()))
            .ToArray();
        return new OperationalRunAuditPage(query.Page, query.PageSize, total, envelopes);
    }

    public async Task<OperationalRunAuditEnvelope?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var incremental = await ReadPayloadAsync(connection, "incremental_run_audits", runId, cancellationToken);
        var evaluations = await ReadEvaluationsAsync(connection, [runId], cancellationToken);
        if (incremental is not null)
            return new OperationalRunAuditEnvelope(null, Deserialize<IncrementalRunAuditRecord>(incremental),
                evaluations.Where(item => item.ParentRunId == runId).ToArray());

        var itemRun = await ReadPayloadAsync(connection, "run_audits", runId, cancellationToken);
        return itemRun is null
            ? null
            : new OperationalRunAuditEnvelope(Deserialize<RunAuditRecord>(itemRun), null,
                evaluations.Where(item => item.RunId == runId || item.ParentRunId == runId).ToArray());
    }

    public async Task<EvaluationAuditRecord?> GetEvaluationAsync(
        Guid runId,
        string evaluationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM evaluation_audits
            WHERE evaluation_id = $evaluationId
              AND (run_id = $runId OR parent_run_id = $runId)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$evaluationId", evaluationId);
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? null : Deserialize<EvaluationAuditRecord>(payload);
    }

    private const string CommonTableExpression = """
        WITH all_runs AS (
            SELECT run_id, started_at_utc, payload_json, 'item' AS source,
                   lower(COALESCE(trigger_type, json_extract(payload_json, '$.triggerType'))) AS trigger_type,
                   CASE WHEN lower(json_extract(payload_json, '$.processingStatus')) = 'error'
                        THEN 'error' ELSE 'completed' END AS run_status
            FROM run_audits
            WHERE COALESCE(parent_run_id, json_extract(payload_json, '$.parentRunId')) IS NULL
            UNION ALL
            SELECT run_id, started_at_utc, payload_json, 'incremental' AS source,
                   lower(COALESCE(trigger_type, json_extract(payload_json, '$.triggerType'))) AS trigger_type,
                   lower(COALESCE(status, json_extract(payload_json, '$.status'))) AS run_status
            FROM incremental_run_audits
        )
        """;

    private static (string Sql, bool HasWorkItem) BuildFilter(OperationalRunQuery query)
    {
        var filters = new List<string>();
        if (query.StartedFromUtc is not null) filters.Add("started_at_utc >= $from");
        if (query.StartedToUtc is not null) filters.Add("started_at_utc <= $to");
        if (query.TriggerType is not null) filters.Add("trigger_type = $trigger");
        if (query.Status is not null) filters.Add("run_status = $status");
        if (!string.IsNullOrWhiteSpace(query.WorkItemId))
        {
            filters.Add("""
                EXISTS (
                    SELECT 1 FROM evaluation_audits e
                    WHERE (e.run_id = all_runs.run_id OR e.parent_run_id = all_runs.run_id)
                      AND COALESCE(e.work_item_id, json_extract(e.payload_json, '$.workItemId')) = $workItemId
                )
                """);
        }
        return (filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters),
            !string.IsNullOrWhiteSpace(query.WorkItemId));
    }

    private static void BindFilter(SqliteCommand command, OperationalRunQuery query, (string Sql, bool HasWorkItem) filter)
    {
        if (query.StartedFromUtc is { } from)
            command.Parameters.AddWithValue("$from", Format(from));
        if (query.StartedToUtc is { } to)
            command.Parameters.AddWithValue("$to", Format(to));
        if (query.TriggerType is { } trigger)
            command.Parameters.AddWithValue("$trigger", trigger.ToString().ToLowerInvariant());
        if (query.Status is { } status)
            command.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
        if (filter.HasWorkItem)
            command.Parameters.AddWithValue("$workItemId", query.WorkItemId!.Trim());
    }

    private static async Task<string?> ReadPayloadAsync(
        SqliteConnection connection,
        string table,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<EvaluationAuditRecord>> ReadEvaluationsAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return [];
        await using var command = connection.CreateCommand();
        var parameters = runIds.Select((_, index) => $"$run{index}").ToArray();
        command.CommandText = $"""
            SELECT payload_json FROM evaluation_audits
            WHERE run_id IN ({string.Join(',', parameters)})
               OR parent_run_id IN ({string.Join(',', parameters)})
            ORDER BY evaluated_at_utc, evaluation_id;
            """;
        for (var index = 0; index < runIds.Count; index++)
            command.Parameters.AddWithValue(parameters[index], runIds[index].ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<EvaluationAuditRecord>();
        while (await reader.ReadAsync(cancellationToken))
            records.Add(Deserialize<EvaluationAuditRecord>(reader.GetString(0)));
        return records;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new InvalidOperationException("An operational audit payload was empty.");

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

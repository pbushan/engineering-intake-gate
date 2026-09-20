using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Evidence;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAnalysisCacheRepository(string databasePath) : IAnalysisCacheRepository
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task<AttachmentEvidenceArtifact?> GetAttachmentAsync(
        string artifactId, string organization, string project, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM attachment_evidence_artifacts
            WHERE artifact_id = $id AND organization = $organization AND project = $project
              AND expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$organization", organization);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        return Deserialize<AttachmentEvidenceArtifact>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task SaveAttachmentAsync(AttachmentEvidenceArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (artifact.ExpiresAtUtc <= artifact.CreatedAtUtc) throw new ArgumentException("Evidence expiry must follow creation.", nameof(artifact));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachment_evidence_artifacts
                (artifact_id, organization, project, attachment_id, content_sha256, created_at_utc, expires_at_utc, payload_json)
            VALUES ($id, $organization, $project, $attachmentId, $hash, $created, $expires, $payload)
            ON CONFLICT(artifact_id) DO UPDATE SET
                created_at_utc = excluded.created_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", artifact.ArtifactId);
        command.Parameters.AddWithValue("$organization", artifact.Organization);
        command.Parameters.AddWithValue("$project", artifact.Project);
        command.Parameters.AddWithValue("$attachmentId", artifact.AttachmentId);
        command.Parameters.AddWithValue("$hash", artifact.ContentSha256);
        command.Parameters.AddWithValue("$created", Format(artifact.CreatedAtUtc));
        command.Parameters.AddWithValue("$expires", Format(artifact.ExpiresAtUtc));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(artifact, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReusableEvaluation?> GetEvaluationAsync(string equivalenceKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        await ReadAsync<ReusableEvaluation>("reusable_evaluations", "equivalence_key", equivalenceKey, nowUtc, cancellationToken);

    public async Task SaveEvaluationAsync(ReusableEvaluation evaluation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reusable_evaluations
                (equivalence_key, origin_evaluation_id, origin_run_id, created_at_utc, expires_at_utc, payload_json)
            VALUES ($key, $evaluationId, $runId, $created, $expires, $payload)
            ON CONFLICT(equivalence_key) DO UPDATE SET
                origin_evaluation_id = excluded.origin_evaluation_id,
                origin_run_id = excluded.origin_run_id,
                created_at_utc = excluded.created_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$key", evaluation.EquivalenceKey);
        command.Parameters.AddWithValue("$evaluationId", evaluation.OriginEvaluationId);
        command.Parameters.AddWithValue("$runId", evaluation.OriginRunId.ToString("D"));
        command.Parameters.AddWithValue("$created", Format(evaluation.CreatedAtUtc));
        command.Parameters.AddWithValue("$expires", Format(evaluation.ExpiresAtUtc));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(evaluation, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveContextAsync(AnalysisContextSnapshot context, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_context_snapshots
                (snapshot_id, work_item_id, created_at_utc, expires_at_utc, payload_json)
            VALUES ($id, $workItemId, $created, $expires, $payload);
            """;
        command.Parameters.AddWithValue("$id", context.SnapshotId);
        command.Parameters.AddWithValue("$workItemId", context.WorkItemId);
        command.Parameters.AddWithValue("$created", Format(context.CreatedAtUtc));
        command.Parameters.AddWithValue("$expires", Format(context.ExpiresAtUtc));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(context, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalysisContextSnapshot?> GetContextAsync(string snapshotId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        await ReadAsync<AnalysisContextSnapshot>("analysis_context_snapshots", "snapshot_id", snapshotId, nowUtc, cancellationToken);

    public async Task SaveScreenshotAsync(SelectedKeyScreenshotArtifact screenshot, CancellationToken cancellationToken = default)
    {
        if (screenshot.Content.Length == 0 || screenshot.ExpiresAtUtc != screenshot.Metadata.ExpiresAtUtc)
            throw new ArgumentException("Screenshot content and expiry are required.", nameof(screenshot));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO selected_key_screenshot_artifacts
                (screenshot_id, source_artifact_id, expires_at_utc, storage_reference, payload_json, media_type, content_blob)
            SELECT $id, $source, $expires, $reference, $payload, $mediaType, $content
            WHERE EXISTS (
                SELECT 1 FROM attachment_evidence_artifacts
                WHERE artifact_id = $source AND organization = $organization AND project = $project)
            ON CONFLICT(screenshot_id) DO UPDATE SET
                expires_at_utc = excluded.expires_at_utc,
                payload_json = excluded.payload_json,
                media_type = excluded.media_type,
                content_blob = excluded.content_blob;
            """;
        command.Parameters.AddWithValue("$id", screenshot.ScreenshotId);
        command.Parameters.AddWithValue("$source", screenshot.SourceArtifactId);
        command.Parameters.AddWithValue("$organization", screenshot.Organization);
        command.Parameters.AddWithValue("$project", screenshot.Project);
        command.Parameters.AddWithValue("$expires", Format(screenshot.ExpiresAtUtc));
        command.Parameters.AddWithValue("$reference", screenshot.Metadata.StorageReference);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(screenshot.Metadata, JsonOptions));
        command.Parameters.AddWithValue("$mediaType", screenshot.MediaType);
        command.Parameters.AddWithValue("$content", screenshot.Content);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The screenshot source artifact was unavailable in the requested scope.");
    }

    public async Task<SelectedKeyScreenshotArtifact?> GetScreenshotAsync(
        string screenshotId, string organization, string project, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_artifact_id, s.payload_json, s.media_type, s.content_blob, s.expires_at_utc
            FROM selected_key_screenshot_artifacts s
            INNER JOIN attachment_evidence_artifacts a ON a.artifact_id = s.source_artifact_id
            WHERE s.screenshot_id = $id AND a.organization = $organization AND a.project = $project
              AND s.expires_at_utc > $now AND a.expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$id", screenshotId);
        command.Parameters.AddWithValue("$organization", organization);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2) || reader.IsDBNull(3)) return null;
        var metadata = Deserialize<SelectedKeyScreenshot>(reader.GetString(1));
        if (metadata is null) return null;
        return new SelectedKeyScreenshotArtifact(screenshotId, reader.GetString(0), organization, project,
            metadata, reader.GetString(2), (byte[])reader[3], DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
    }

    public async Task<EvidenceCleanupResult> DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var screenshots = await DeleteAsync("selected_key_screenshot_artifacts", transaction, nowUtc, cancellationToken);
        var contexts = await DeleteAsync("analysis_context_snapshots", transaction, nowUtc, cancellationToken);
        var evaluations = await DeleteAsync("reusable_evaluations", transaction, nowUtc, cancellationToken);
        var attachments = await DeleteAsync("attachment_evidence_artifacts", transaction, nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EvidenceCleanupResult(contexts, attachments, evaluations, screenshots);
    }

    private async Task<T?> ReadAsync<T>(string table, string keyColumn, string key, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE {keyColumn} = $key AND expires_at_utc > $now;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        return Deserialize<T>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    private static async Task<int> DeleteAsync(string table, System.Data.Common.DbTransaction transaction, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)transaction.Connection!;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM {table} WHERE expires_at_utc <= $now;";
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static T? Deserialize<T>(string? value) => value is null ? default : JsonSerializer.Deserialize<T>(value, JsonOptions);
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

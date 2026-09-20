using IntakeGate.Application.Evidence;
using IntakeGate.Application.Evaluation;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteAnalysisCacheRepositoryTests
{
    [Fact]
    public async Task CACHE_003_ArtifactLookupIsScopeBoundAndVersionedByItsImmutableKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"intake-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var repository = new SqliteAnalysisCacheRepository(path);
            var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
            var artifact = Artifact(now);
            await repository.SaveAttachmentAsync(artifact);

            Assert.NotNull(await repository.GetAttachmentAsync(artifact.ArtifactId, "https://org", "project", now));
            Assert.Null(await repository.GetAttachmentAsync(artifact.ArtifactId, "https://other-org", "project", now));
            Assert.Null(await repository.GetAttachmentAsync(artifact.ArtifactId, "https://org", "other-project", now));
            Assert.Null(await repository.GetAttachmentAsync(artifact.ArtifactId, "https://org", "project", artifact.ExpiresAtUtc));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RET_001_ExpiredEvidenceAndSelectedScreenshotReferencesAreDeletedIdempotently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"intake-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var repository = new SqliteAnalysisCacheRepository(path);
            var created = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
            var artifact = Artifact(created);
            await repository.SaveAttachmentAsync(artifact);
            var context = Context(created);
            await repository.SaveContextAsync(context);
            var originRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            const string originEvaluationId = "22222222-2222-2222-2222-222222222222";
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO run_audits (run_id, started_at_utc, payload_json)
                    VALUES ($run, $created, '{}');
                    INSERT INTO evaluation_audits (evaluation_id, run_id, evaluated_at_utc, payload_json)
                    VALUES ($evaluation, $run, $created, '{}');
                    INSERT INTO selected_key_screenshot_artifacts
                        (screenshot_id, source_artifact_id, expires_at_utc, storage_reference, payload_json)
                    VALUES ('shot', $artifact, $expires, 'future://shot', '{}');
                    """;
                command.Parameters.AddWithValue("$run", originRunId.ToString("D"));
                command.Parameters.AddWithValue("$evaluation", originEvaluationId);
                command.Parameters.AddWithValue("$created", created.ToString("O"));
                command.Parameters.AddWithValue("$artifact", artifact.ArtifactId);
                command.Parameters.AddWithValue("$expires", artifact.ExpiresAtUtc.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            await repository.SaveEvaluationAsync(new ReusableEvaluation(
                "equivalence", originEvaluationId, originRunId,
                new EvaluationResult(originEvaluationId, IntakeDecision.Pass, "policy", "1", "sha256:policy",
                    "prompt", [], [], [], [], "summary", "provider", "model"),
                "reported-model", created, artifact.ExpiresAtUtc));

            var first = await repository.DeleteExpiredAsync(artifact.ExpiresAtUtc);
            var second = await repository.DeleteExpiredAsync(artifact.ExpiresAtUtc.AddDays(1));

            Assert.Equal(1, first.AttachmentArtifactsDeleted);
            Assert.Equal(1, first.AnalysisContextsDeleted);
            Assert.Equal(1, first.EvaluationEntriesDeleted);
            Assert.Equal(1, first.ScreenshotReferencesDeleted);
            Assert.Equal(new EvidenceCleanupResult(0, 0, 0, 0), second);
            Assert.Equal(1L, await ScalarAsync<long>(path, "SELECT COUNT(*) FROM evaluation_audits;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PH2_ScreenshotLookupIsScopeBoundAndExpiresWithItsSourceArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"intake-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var repository = new SqliteAnalysisCacheRepository(path);
            var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
            var artifact = Artifact(now);
            await repository.SaveAttachmentAsync(artifact);
            var screenshotId = AnalysisFingerprint.Sha256("selected screenshot");
            var metadata = new SelectedKeyScreenshot(
                artifact.AttachmentId, artifact.ContentSha256, 12.7, AnalysisFingerprint.Sha256("jpeg"),
                640, 360, 4, "sampling-v1", screenshotId, artifact.ExpiresAtUtc)
            {
                Observation = "Visible error E-42.",
                PipelineVersion = "selection-v1"
            };
            await repository.SaveScreenshotAsync(new SelectedKeyScreenshotArtifact(
                screenshotId, artifact.ArtifactId, artifact.Organization, artifact.Project,
                metadata, "image/jpeg", [1, 2, 3, 4], artifact.ExpiresAtUtc));

            var stored = await repository.GetScreenshotAsync(
                screenshotId, artifact.Organization, artifact.Project, now);

            Assert.NotNull(stored);
            Assert.Equal("image/jpeg", stored.MediaType);
            Assert.Equal([1, 2, 3, 4], stored.Content);
            Assert.DoesNotContain(Path.GetTempPath(), stored.Metadata.StorageReference, StringComparison.Ordinal);
            Assert.Null(await repository.GetScreenshotAsync(screenshotId, "https://other-org", artifact.Project, now));
            Assert.Null(await repository.GetScreenshotAsync(screenshotId, artifact.Organization, "other-project", now));
            Assert.Null(await repository.GetScreenshotAsync(
                screenshotId, artifact.Organization, artifact.Project, artifact.ExpiresAtUtc));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static AttachmentEvidenceArtifact Artifact(DateTimeOffset now)
    {
        var hash = AnalysisFingerprint.Sha256("content");
        var limits = AnalysisFingerprint.Sha256("limits");
        var id = AnalysisFingerprint.Artifact("https://org", "project", "attachment", hash, "text", "1", limits);
        return new AttachmentEvidenceArtifact(id, "https://org", "project", "attachment", "evidence.txt",
            "text/plain", 7, hash, "text", "1", AnalysisContextVersions.NormalizedEvidence,
            limits, "safe normalized evidence", AttachmentProcessingStatus.Processed,
            AttachmentInspectionMode.Text, false, false, null, null, null, [], now, now.AddDays(30));
    }

    private static AnalysisContextSnapshot Context(DateTimeOffset now)
    {
        var evidence = new EvaluationEvidence("42", "7", "Bug", "Title", [], "Description", [], [], [], [],
            new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 16),
            new RedactionMetadata(false, 0, new Dictionary<string, int>()));
        return new AnalysisContextSnapshot("context", AnalysisContextVersions.Schema,
            AnalysisContextVersions.NormalizedEvidence, "42", "7", "https://org", "project", evidence, [],
            "source", "manifest", "evidence", "prompt", "profile", "1", "policy", "1", "sha256:policy",
            now, now.AddDays(30));
    }

    private static async Task<T> ScalarAsync<T>(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a scalar value.");
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

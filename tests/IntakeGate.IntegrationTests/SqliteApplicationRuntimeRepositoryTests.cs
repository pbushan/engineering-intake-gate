using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Domain;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteApplicationRuntimeRepositoryTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"intake-gate-{Guid.NewGuid():N}");

    [Fact]
    public async Task DB_001_DB_002_InitializesWritesAndReadsRuntimeRecord()
    {
        var databasePath = Path.Combine(_testDirectory, "nested", "operational.db");
        var repository = new SqliteApplicationRuntimeRepository(databasePath);
        var expected = new ApplicationRuntimeRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 10, 16, 45, 12, TimeSpan.Zero).AddTicks(3456),
            "1.0.0");

        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        await repository.AddAsync(expected);
        var records = await repository.ListAsync();

        var actual = Assert.Single(records);
        Assert.Equal(expected, actual);
        Assert.Equal(TimeSpan.Zero, actual.StartedAtUtc.Offset);
        Assert.True(await repository.CanAccessAsync());
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public async Task NFR_003_NewRepositoryInstanceReadsExistingState()
    {
        var databasePath = Path.Combine(_testDirectory, "operational.db");
        var firstRepository = new SqliteApplicationRuntimeRepository(databasePath);
        var expected = new ApplicationRuntimeRecord(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "1.0.0");
        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        await firstRepository.AddAsync(expected);

        var restartedRepository = new SqliteApplicationRuntimeRepository(databasePath);
        var records = await restartedRepository.ListAsync();

        Assert.Equal(expected, Assert.Single(records));
    }

    [Fact]
    public async Task DB_001_ReadinessDependencyFailsBeforeDatabaseInitialization()
    {
        var repository = new SqliteApplicationRuntimeRepository(
            Path.Combine(_testDirectory, "uninitialized.db"));

        Assert.False(await repository.CanAccessAsync());
    }

    [Fact]
    public async Task DB_001_AUD_001_NFR_007_AuditReconstructsAndSurvivesRestart()
    {
        var databasePath = Path.Combine(_testDirectory, "audit.db");
        var first = new SqliteAuditRepository(databasePath);
        var run = Run();
        var evaluation = Evaluation(run.RunId);
        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        await first.SaveAsync(run, evaluation);

        var restarted = new SqliteAuditRepository(databasePath);
        var actualRun = await restarted.GetRunAsync(run.RunId);
        var actualEvaluation = await restarted.GetEvaluationAsync(evaluation.EvaluationId);

        Assert.Equal(run, actualRun);
        Assert.NotNull(actualEvaluation);
        Assert.Equal(evaluation.EvaluationId, actualEvaluation!.EvaluationId);
        Assert.Equal(evaluation.RunId, actualEvaluation.RunId);
        Assert.Equal("work-item-42", actualEvaluation!.WorkItemId);
        Assert.Equal("17", actualEvaluation.EvaluatedRevision);
        Assert.Equal("profile", actualEvaluation.ProfileId);
        Assert.Equal("policy", actualEvaluation.PolicyId);
        Assert.Equal("1", actualEvaluation.PolicyVersion);
        Assert.Equal("sha256:abc", actualEvaluation.PolicyFingerprint);
        Assert.Equal("fake", actualEvaluation.ProviderIdentifier);
        Assert.Equal("scripted", actualEvaluation.ModelIdentifier);
        Assert.Equal("intake-evaluator-v1", actualEvaluation.PromptVersion);
        Assert.Equal(["problem"], actualEvaluation.ApplicableCriteria);
        Assert.Equal(["problem"], actualEvaluation.SatisfiedCriteria);
        Assert.Equal(IntakeDecision.Pass, actualEvaluation.Decision);
        Assert.Equal(ExecutionMode.DryRun, actualEvaluation.ExecutionMode);
        Assert.Equal(EvaluationProcessingStatus.Completed, actualEvaluation.ProcessingStatus);
        Assert.Equal(new TokenUsage(3, 2, 5), actualEvaluation.TokenUsage);
        Assert.Equal(new EstimatedCost(0.01m, "TEST"), actualEvaluation.EstimatedCost);
        Assert.Single(actualEvaluation.ProposedMutations);
        Assert.Empty(actualEvaluation.AttemptedMutations);
        Assert.Empty(actualEvaluation.MutationOutcomes);
    }

    [Fact]
    public async Task ERR_005_E10_PendingReconciliationSurvivesRepositoryRestart()
    {
        var databasePath = Path.Combine(_testDirectory, "reconciliation-restart.db");
        var first = new SqliteAuditRepository(databasePath);
        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        var run = Run();
        var evaluation = Evaluation(run.RunId);
        await first.SaveAsync(run, evaluation);
        var now = DateTimeOffset.Parse("2026-09-10T12:00:02Z");
        var pending = new MutationReconciliationRecord(evaluation.EvaluationId, "profile", "work-item-42", "17", "18",
            evaluation.ProposedMutations, ["VALID"], [ProposedMutationType.AddTag], ReconciliationStatus.Pending, now, now, 1, "AzureDevOpsMutationUnavailable");
        await first.CreatePendingAsync(pending);

        var restarted = new SqliteAuditRepository(databasePath);
        var restored = Assert.Single(await restarted.GetPendingAsync("profile"));

        Assert.Equal(evaluation.EvaluationId, restored.EvaluationId);
        Assert.Equal(ReconciliationStatus.Pending, restored.Status);
        Assert.Equal(pending.ExpectedRevision, restored.ExpectedRevision);
        Assert.Equal(pending.IntendedMutations, restored.IntendedMutations);
        Assert.Equal(pending.CompletedMutationTypes, restored.CompletedMutationTypes);
    }

    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static RunAuditRecord Run() => new(
        RunId, RunTriggerType.ManualWorkItem, DateTimeOffset.Parse("2026-09-10T12:00:00Z"),
        DateTimeOffset.Parse("2026-09-10T12:00:01Z"), ExecutionMode.DryRun, "profile", "1", "sha256:abc",
        EvaluationProcessingStatus.Completed, 1, 1, 0, 0, 0, new TokenUsage(3, 2, 5),
        new EstimatedCost(0.01m, "TEST"), null);

    private static EvaluationAuditRecord Evaluation(Guid runId) => new(
        "11111111-2222-3333-4444-555555555555", runId, "work-item-42", "17",
        DateTimeOffset.Parse("2026-09-10T12:00:01Z"), "manual fixture", "profile", "policy", "1",
        "sha256:abc", "intake-evaluator-v1", "fake", "scripted", ["problem"], ["problem"], [], [],
        "Safe summary.", true, 1, [new RedactionCategoryCount("password-assignment", 1)], 1, 1, 0, 0, true, false,
        IntakeDecision.Pass, EvaluationProcessingStatus.Completed, ExecutionMode.DryRun,
        new TokenUsage(3, 2, 5), new EstimatedCost(0.01m, "TEST"),
        [ProposedMutation.AddTag("VALID")], [], [], []);

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}

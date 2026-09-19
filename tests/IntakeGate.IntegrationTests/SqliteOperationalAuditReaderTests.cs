using IntakeGate.Application.Audit;
using IntakeGate.Application.AiPricing;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.WorkItems;
using IntakeGate.Infrastructure.Persistence;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteOperationalAuditReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-ops-{Guid.NewGuid():N}");

    [Fact]
    public async Task OPS_004_OPS_009_HistoryIsPagedNewestFirstAndIncrementalChildrenAreContained()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "history.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var audits = new SqliteAuditRepository(database);
        var incremental = new SqliteIncrementalDiscoveryRepository(database);
        var reader = new SqliteOperationalAuditReader(database);
        var manualOld = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var manualNew = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var parent = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var child = Guid.Parse("40000000-0000-0000-0000-000000000004");

        await audits.SaveAsync(Run(manualOld, "2026-09-13T10:00:00Z"), Evaluation(manualOld, "41"));
        await audits.SaveAsync(Run(manualNew, "2026-09-13T11:00:00Z"), Evaluation(manualNew, "42"));
        await incremental.SaveRunAuditAsync(new IncrementalRunAuditRecord(
            parent, RunTriggerType.ManualIncremental, At("2026-09-13T12:00:00Z"), At("2026-09-13T12:01:00Z"),
            ExecutionMode.DryRun, "profile", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            null, At("2026-09-13T12:00:10Z"), 1, 1, 0, 1, 1, 0, 0, 0,
            new TokenUsage(7, 3, 10), new EstimatedCost(0.12m, "USD"),
            IncrementalRunStatus.Completed, null)
        { TriggeredBy = new AuditActor(Guid.Empty, "system") });
        await audits.SaveAsync(
            Run(child, "2026-09-13T12:00:20Z") with { ParentRunId = parent },
            Evaluation(child, "43") with { ParentRunId = parent });

        var page = await reader.ListRunsAsync(new OperationalRunQuery(1, 2, null, null, null, null, null));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal([parent, manualNew], page.Runs.Select(run => run.RunId));
        Assert.Single(page.Runs[0].Evaluations);
        Assert.Equal("43", page.Runs[0].Evaluations[0].WorkItemId);

        var filtered = await reader.ListRunsAsync(new OperationalRunQuery(1, 10, null, null,
            RunTriggerType.ManualIncremental, OperationalRunStatus.Completed, "43"));
        Assert.Equal(parent, Assert.Single(filtered.Runs).RunId);
    }

    [Fact]
    public async Task OPS_006_OPS_007_SuppressionAndActualEffectsComeOnlyFromPersistedAudit()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "effects.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var audits = new SqliteAuditRepository(database);
        var run = Run(Guid.NewGuid(), "2026-09-13T12:00:00Z");
        var evaluation = Evaluation(run.RunId, "42") with
        {
            ProposedMutations = [ProposedMutation.AddTag("READY"), ProposedMutation.PostComment("safe", "marker")],
            AiInteractions = [new AiInteractionCostRecord(
                1, "openai", "requested-model", "openai", "reported-model", "req-1",
                new TokenUsage(7, 3, 10), 0.000007m, 0.000006m, 0.000013m,
                new AppliedAiPricing(1m, 2m, "USD", "Test catalog",
                    new Uri("https://example.test/pricing"), "test-v1", At("2026-09-01T00:00:00Z"),
                    At("2026-09-13T11:00:00Z"),
                    AiModelPricingSourceKind.BundledCatalog, false))]
        };
        await audits.SaveAsync(run, evaluation);
        await audits.UpdateMutationAuditAsync(evaluation.EvaluationId,
            [ProposedMutation.AddTag("READY")],
            [new MutationOutcome(ProposedMutationType.AddTag, true, null)],
            MutationExecutionState.Completed, []);
        await audits.UpdateSuppressionAuditAsync(evaluation.EvaluationId, true,
            "MateriallyUnchangedAssessment", false);

        var restored = await new SqliteOperationalAuditReader(database).GetRunAsync(run.RunId);
        var item = Assert.Single(restored!.Evaluations);
        Assert.True(item.UpdateSuppressed);
        Assert.False(item.MateriallyChanged);
        Assert.Equal("MateriallyUnchangedAssessment", item.SuppressionReason);
        Assert.Single(item.MutationOutcomes);
        var interaction = Assert.Single(item.AiInteractions);
        Assert.Equal("reported-model", interaction.ModelUsedForPricing);
        Assert.Equal(0.000013m, interaction.EstimatedTotalCost);
        Assert.Equal("test-v1", interaction.Pricing!.CatalogVersion);
        Assert.Equal(ProposedMutationType.AddTag, item.MutationOutcomes[0].Type);
        Assert.DoesNotContain(item.MutationOutcomes, outcome => outcome.Type == ProposedMutationType.PostComment);
    }

    private static RunAuditRecord Run(Guid id, string at) => new(
        id, RunTriggerType.ManualWorkItem, At(at), At(at).AddSeconds(1), ExecutionMode.DryRun,
        "profile", "1", "sha256:policy", EvaluationProcessingStatus.Completed,
        1, 1, 0, 0, 0, new TokenUsage(7, 3, 10), new EstimatedCost(0.12m, "USD"), null)
    {
        TriggeredBy = new AuditActor(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "operator")
    };

    private static EvaluationAuditRecord Evaluation(Guid runId, string workItemId) => new(
        Guid.NewGuid().ToString("D"), runId, workItemId, "7", At("2026-09-13T12:00:01Z"),
        "ConfiguredSavedQueryMember", "profile", "policy", "1", "sha256:policy",
        "intake-evaluator-v1", "fake", "model", ["problem"], ["problem"], [], [], "Safe summary.",
        false, 0, [], 0, 0, 0, 0, false, false, IntakeDecision.Pass,
        EvaluationProcessingStatus.Completed, ExecutionMode.DryRun,
        new TokenUsage(7, 3, 10), new EstimatedCost(0.12m, "USD"),
        [ProposedMutation.AddTag("READY")], [], [], [])
    {
        TriggeredBy = new AuditActor(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "operator")
    };

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

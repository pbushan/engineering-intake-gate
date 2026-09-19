using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.WorkItems;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteHomeSummaryReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-home-{Guid.NewGuid():N}");

    [Fact]
    public async Task HOME_001_HOME_002_LockedOutcomesRateInputsSuppressionAndHistoricalCostAreAggregated()
    {
        var database = await DatabaseAsync();
        var audits = new SqliteAuditRepository(database);
        var start = At("2026-08-14T12:00:00Z");
        var end = At("2026-09-13T12:00:00Z");

        await SaveAsync(audits, start, IntakeDecision.Pass, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(0.100000000000000001m, "USD"), suppressed: true);
        await SaveAsync(audits, start.AddDays(1), IntakeDecision.Fail, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(0.200000000000000002m, "usd"));
        await SaveAsync(audits, start.AddDays(2), null, EvaluationProcessingStatus.Error,
            WorkItemEligibility.Eligible, new EstimatedCost(0.300000000000000003m, "USD")
            {
                Complete = false,
                PricedInteractions = 1,
                TotalInteractions = 2
            });
        await SaveAsync(audits, start.AddDays(3), null, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.NotEligible, null);
        await SaveAsync(audits, start.AddDays(4), null, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, null);
        // A generic no-op is not duplicate suppression.
        await SaveAsync(audits, start.AddDays(5), IntakeDecision.Pass, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, null, suppressed: false);
        // Both half-open window boundaries are explicit: start is included and end is excluded.
        await SaveAsync(audits, start.AddTicks(-1), IntakeDecision.Pass, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(10m, "USD"), suppressed: true);
        await SaveAsync(audits, end, IntakeDecision.Fail, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(10m, "USD"), suppressed: true);

        var result = await new SqliteHomeSummaryReader(database).GetAsync(start, end);

        Assert.Equal(4, result.EvaluatedCount);
        Assert.Equal(2, result.EngineeringReadyCount);
        Assert.Equal(1, result.IntakeIncompleteCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, result.NotEligibleCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, result.DuplicateUpdatesSuppressedCount);
        Assert.Equal(0.600000000000000006m, result.EstimatedAiCost!.Amount);
        Assert.Equal("USD", result.EstimatedAiCost.Currency);
        Assert.Equal(3, result.EvaluationsWithEstimatedCost);
        Assert.Equal(2, result.EvaluationsWithCompleteEstimatedCost);
        Assert.Equal(1, result.EvaluationsWithPartialEstimatedCost);
        Assert.Equal(1, result.EvaluationsWithoutEstimatedCost);
        Assert.False(result.EstimatedCostComplete);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task HOME_001_SupportedWindowsUseTheRequestedUtcInterval(int days)
    {
        var database = await DatabaseAsync();
        var end = At("2026-09-13T12:00:00Z");
        var start = end.AddDays(-days);
        var audits = new SqliteAuditRepository(database);
        await SaveAsync(audits, start, IntakeDecision.Pass, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(0.01m, "USD"));
        await SaveAsync(audits, start.AddTicks(-1), IntakeDecision.Fail, EvaluationProcessingStatus.Completed,
            WorkItemEligibility.Eligible, new EstimatedCost(1m, "USD"));

        var result = await new SqliteHomeSummaryReader(database).GetAsync(start, end);

        Assert.Equal(start, result.WindowStartInclusiveUtc);
        Assert.Equal(end, result.WindowEndExclusiveUtc);
        Assert.Equal(1, result.EvaluatedCount);
        Assert.Equal(1, result.EngineeringReadyCount);
    }

    [Fact]
    public async Task HOME_003_EmptyWindowHasNoInventedRateOrCostInputs()
    {
        var database = await DatabaseAsync();

        var result = await new SqliteHomeSummaryReader(database).GetAsync(
            At("2026-09-06T12:00:00Z"), At("2026-09-13T12:00:00Z"));

        Assert.Equal(0, result.EvaluatedCount);
        Assert.Equal(0, result.EngineeringReadyCount + result.IntakeIncompleteCount);
        Assert.Null(result.EstimatedAiCost);
        Assert.True(result.EstimatedCostComplete);
    }

    [Fact]
    public async Task HOME_004_EvaluationTimestampIndexSupportsBoundedAggregate()
    {
        var database = await DatabaseAsync();
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT evaluation_id FROM evaluation_audits
            WHERE evaluated_at_utc >= '2026-09-01T00:00:00.0000000+00:00'
              AND evaluated_at_utc < '2026-10-01T00:00:00.0000000+00:00';
            """;
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, line => line.Contains("ix_evaluation_audits_evaluated", StringComparison.Ordinal));
    }

    private async Task<string> DatabaseAsync()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        return database;
    }

    private static async Task SaveAsync(
        SqliteAuditRepository audits,
        DateTimeOffset at,
        IntakeDecision? decision,
        EvaluationProcessingStatus status,
        WorkItemEligibility eligibility,
        EstimatedCost? cost,
        bool suppressed = false)
    {
        var runId = Guid.NewGuid();
        var pass = decision == IntakeDecision.Pass && status != EvaluationProcessingStatus.Error ? 1 : 0;
        var fail = decision == IntakeDecision.Fail && status != EvaluationProcessingStatus.Error ? 1 : 0;
        var error = status == EvaluationProcessingStatus.Error ? 1 : 0;
        var run = new RunAuditRecord(runId, RunTriggerType.ManualWorkItem, at, at.AddSeconds(1),
            ExecutionMode.DryRun, "profile", "1", "sha256:policy", status, 1, pass, fail, error, 0,
            null, cost, error == 1 ? "TechnicalFailure" : null)
        { Eligibility = eligibility };
        var evaluation = new EvaluationAuditRecord(
            Guid.NewGuid().ToString("D"), runId, Guid.NewGuid().ToString("N"), "7", at,
            "ConfiguredSavedQueryMember", "profile", "policy", "1", "sha256:policy",
            "intake-evaluator-v1", "fake", "model", [], [], [], [], null,
            false, 0, [], 0, 0, 0, 0, false, false, decision, status, ExecutionMode.DryRun,
            null, cost, [], [], [], error == 1 ? ["TechnicalFailure"] : [])
        {
            Eligibility = eligibility,
            UpdateSuppressed = suppressed,
            MateriallyChanged = suppressed ? false : null,
            SuppressionReason = suppressed ? "MateriallyUnchangedAssessment" : null
        };
        await audits.SaveAsync(run, evaluation);
    }

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

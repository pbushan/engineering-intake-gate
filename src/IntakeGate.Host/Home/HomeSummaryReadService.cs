using IntakeGate.Application.Audit;
using IntakeGate.Application.Time;
using IntakeGate.Host.Operations;
using IntakeGate.Host.Supportability;

namespace IntakeGate.Host.Home;

public sealed class HomeSummaryReadService(
    IHomeSummaryReader summaryReader,
    OperationalRunReadService operationalRuns,
    SupportabilityReadService supportability,
    IClock clock)
{
    private const int RecentRunLimit = 8;
    private const int RecentFailureWarningLimit = 3;

    public async Task<HomeSummaryResponse> GetAsync(int windowDays, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var start = now.AddDays(-windowDays);
        var aggregate = await summaryReader.GetAsync(start, now, cancellationToken);
        var recent = await operationalRuns.ListAsync(new OperationalRunQuery(
            1, RecentRunLimit, start, now.AddTicks(-1), null, null, null), cancellationToken);
        // Health is derived only from persisted state; rendering Home never invokes a provider.
        var health = await supportability.GetHealthAsync(includeAdminDiagnostics: false, cancellationToken);
        var denominator = aggregate.EngineeringReadyCount + aggregate.IntakeIncompleteCount;
        decimal? rate = denominator == 0
            ? null
            : decimal.Round(aggregate.EngineeringReadyCount * 100m / denominator, 1,
                MidpointRounding.AwayFromZero);

        return new HomeSummaryResponse(
            windowDays,
            now,
            aggregate.WindowStartInclusiveUtc,
            aggregate.WindowEndExclusiveUtc,
            aggregate.EvaluatedCount,
            aggregate.EngineeringReadyCount,
            aggregate.IntakeIncompleteCount,
            aggregate.ErrorCount,
            aggregate.NotEligibleCount,
            aggregate.SkippedCount,
            aggregate.DuplicateUpdatesSuppressedCount,
            new EngineeringReadyRateResponse(
                aggregate.EngineeringReadyCount, denominator, rate),
            new EstimatedAiCostSummaryResponse(
                aggregate.EstimatedAiCost?.Amount,
                aggregate.EstimatedAiCost?.Currency,
                aggregate.EvaluationsWithEstimatedCost,
                aggregate.EvaluationsWithCompleteEstimatedCost,
                aggregate.EvaluationsWithPartialEstimatedCost,
                aggregate.EvaluationsWithoutEstimatedCost,
                aggregate.EstimatedCostComplete),
            recent.Items,
            Warnings(health));
    }

    private static IReadOnlyList<HomeHealthWarningResponse> Warnings(SystemHealthResponse health)
    {
        var warnings = new List<HomeHealthWarningResponse>();
        if (!health.Setup.Complete)
            warnings.Add(new("SetupIncomplete", "Setup incomplete",
                "Required installation setup is not complete.", "/system-health"));
        if (health.Runtime.Status == RuntimeHealthStatus.ActivationFailed)
            warnings.Add(new("RuntimeActivationFailed", "Runtime activation needs attention",
                "The saved configuration is not the active runtime generation.", "/system-health"));
        else if (health.Runtime.Status == RuntimeHealthStatus.NoActiveGeneration)
            warnings.Add(new("NoActiveGeneration", "No active runtime generation",
                "Complete setup and activate a validated configuration before running evaluations.", "/system-health"));
        if (health.AzureDevOps.Status != ConnectionHealthStatus.Verified)
            warnings.Add(new("AzureDevOpsNotVerified", "Azure DevOps needs attention",
                "Status is based on the latest persisted verification; no live check was performed.", "/system-health"));
        if (health.Ai.Status != ConnectionHealthStatus.Verified)
            warnings.Add(new("AiProviderNotVerified", "AI provider needs attention",
                "Status is based on the latest persisted verification; no live check was performed.", "/system-health"));
        if (health.Scheduler.Status == SchedulerHealthStatus.ActivationFailed)
            warnings.Add(new("SchedulerActivationFailed", "Scheduler needs attention",
                "Scheduled execution cannot continue with the current runtime activation state.", "/system-health"));

        warnings.AddRange(health.RecentFailures.Take(RecentFailureWarningLimit).Select(failure =>
            new HomeHealthWarningResponse(
                $"RecentRunFailure:{failure.RunId:D}",
                "Recent run needs attention",
                failure.Message,
                $"/runs/{failure.RunId:D}")));
        return warnings;
    }
}

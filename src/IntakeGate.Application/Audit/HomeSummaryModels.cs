namespace IntakeGate.Application.Audit;

/// <summary>
/// Authoritative bounded assessment aggregate for Home. Outcomes are classified from the
/// persisted evaluation audit; discovered work and top-level run totals are deliberately not
/// substituted for evaluated tickets.
/// </summary>
public sealed record HomeSummaryAggregate(
    DateTimeOffset WindowStartInclusiveUtc,
    DateTimeOffset WindowEndExclusiveUtc,
    int EvaluatedCount,
    int EngineeringReadyCount,
    int IntakeIncompleteCount,
    int ErrorCount,
    int NotEligibleCount,
    int SkippedCount,
    int DuplicateUpdatesSuppressedCount,
    EstimatedCost? EstimatedAiCost,
    int EvaluationsWithEstimatedCost,
    int EvaluationsWithCompleteEstimatedCost,
    int EvaluationsWithPartialEstimatedCost,
    int EvaluationsWithoutEstimatedCost,
    bool EstimatedCostComplete);

public interface IHomeSummaryReader
{
    Task<HomeSummaryAggregate> GetAsync(
        DateTimeOffset windowStartInclusiveUtc,
        DateTimeOffset windowEndExclusiveUtc,
        CancellationToken cancellationToken = default);
}

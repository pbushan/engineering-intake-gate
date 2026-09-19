using IntakeGate.Host.Operations;

namespace IntakeGate.Host.Home;

public sealed record EngineeringReadyRateResponse(
    int Numerator,
    int Denominator,
    decimal? Percentage);

public sealed record EstimatedAiCostSummaryResponse(
    decimal? Amount,
    string? Currency,
    int EvaluationsWithEstimate,
    int EvaluationsWithCompleteEstimate,
    int EvaluationsWithPartialEstimate,
    int EvaluationsWithoutEstimate,
    bool Complete);

public sealed record HomeHealthWarningResponse(
    string Code,
    string Title,
    string Message,
    string DetailUrl);

public sealed record HomeSummaryResponse(
    int WindowDays,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset WindowStartInclusiveUtc,
    DateTimeOffset WindowEndExclusiveUtc,
    int EvaluatedCount,
    int EngineeringReadyCount,
    int IntakeIncompleteCount,
    int ErrorCount,
    int NotEligibleCount,
    int SkippedCount,
    int DuplicateUpdatesSuppressedCount,
    EngineeringReadyRateResponse EngineeringReadyRate,
    EstimatedAiCostSummaryResponse EstimatedAiCost,
    IReadOnlyList<RunSummaryResponse> RecentRuns,
    IReadOnlyList<HomeHealthWarningResponse> HealthWarnings);

using IntakeGate.Application.Audit;

namespace IntakeGate.Host.Audit;

public sealed class StructuredAiCostAccountingLog(ILogger<StructuredAiCostAccountingLog> logger)
    : IAiCostAccountingLog
{
    public void PricingResolved(string provider, string model, bool refreshed, bool stale) =>
        logger.LogInformation(
            "AI pricing resolved. Event={EventName} Provider={Provider} Model={Model} Refreshed={Refreshed} Stale={Stale}",
            "AiPricingResolved", provider, model, refreshed, stale);

    public void PricingUnavailable(string provider, string model, bool refreshFailed) =>
        logger.LogWarning(
            "AI pricing is unavailable. Event={EventName} Provider={Provider} Model={Model} RefreshFailed={RefreshFailed}",
            "AiPricingUnavailable", provider, model, refreshFailed);

    public void MissingUsage(string provider, string model, int attempt) =>
        logger.LogWarning(
            "AI usage metadata is unavailable for cost accounting. Event={EventName} Provider={Provider} Model={Model} Attempt={Attempt}",
            "AiUsageUnavailable", provider, model, attempt);

    public void CalculationFailed(string provider, string model, int attempt, string category) =>
        logger.LogWarning(
            "AI cost calculation was unavailable. Event={EventName} Provider={Provider} Model={Model} Attempt={Attempt} FailureCategory={FailureCategory}",
            "AiCostCalculationUnavailable", provider, model, attempt, category);
}

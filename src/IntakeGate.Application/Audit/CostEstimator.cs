using IntakeGate.Application.Configuration;

namespace IntakeGate.Application.Audit;

public interface ICostEstimator
{
    EstimatedCost? Estimate(AiConfiguration configuration, string model, TokenUsage? usage);
}

public sealed class CostEstimator : ICostEstimator
{
    public EstimatedCost? Estimate(AiConfiguration configuration, string model, TokenUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (usage is null) return null;

        var pricing = configuration.Pricing.SingleOrDefault(item =>
            string.Equals(item.Provider, configuration.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model, model, StringComparison.Ordinal));
        if (pricing is null) return null;

        var amount = usage.InputTokens / 1_000_000m * pricing.InputPerMillionTokens
                   + usage.OutputTokens / 1_000_000m * pricing.OutputPerMillionTokens;
        return new EstimatedCost(amount, pricing.Currency) { PricingIdentity = pricing.Identity };
    }
}

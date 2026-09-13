using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class CostEstimatorTests
{
    [Fact]
    public void NFR_008_KnownSyntheticPricingUsesExactDecimalArithmetic()
    {
        var configuration = new AiConfiguration("openai", "synthetic-model", new CredentialReference("KEY"))
        {
            Pricing = [new ModelPricing("openai", "synthetic-model", 2.5m, 10m, "USD", "synthetic-v1")]
        };
        var result = new CostEstimator().Estimate(configuration, "synthetic-model", new TokenUsage(2_000, 500, 2_500));
        Assert.Equal(0.010m, result!.Amount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal("synthetic-v1", result.PricingIdentity);
    }

    [Fact]
    public void NFR_008_UnknownPricingReturnsNull()
    {
        var configuration = new AiConfiguration("anthropic", "unknown", new CredentialReference("KEY"));
        Assert.Null(new CostEstimator().Estimate(configuration, "unknown", new TokenUsage(10, 10, 20)));
    }
}

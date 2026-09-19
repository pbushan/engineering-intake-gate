using IntakeGate.Application.AiPricing;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Time;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class CostEstimatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    [Fact]
    public void NFR_008_InputAndOutputUseExactDecimalArithmetic()
    {
        var result = new CostEstimator().Estimate(Record("openai", "synthetic-model", 2.5m, 10m),
            new TokenUsage(2_000, 500, 2_500));

        Assert.Equal(0.005m, result.InputAmount);
        Assert.Equal(0.005m, result.OutputAmount);
        Assert.Equal(0.010m, result.TotalAmount);
        Assert.Equal("USD", result.Currency);
    }

    [Theory]
    [InlineData(1_000, 0, "0.0025")]
    [InlineData(0, 1_000, "0.01")]
    [InlineData(0, 0, "0")]
    public void AI_COST_001_InputOnlyOutputOnlyAndZeroTokensAreExact(
        int input, int output, string expected)
    {
        var result = new CostEstimator().Estimate(Record("openai", "synthetic-model", 2.5m, 10m),
            new TokenUsage(input, output, input + output));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            result.TotalAmount);
    }

    [Fact]
    public async Task AI_COST_002_ActualReportedModelTakesPrecedenceAndRetriesAreCounted()
    {
        var accounting = Service(
            Quote("openai", "reported-model", 1m, 10m),
            Quote("openai", "requested-model", 99m, 99m));
        var result = await accounting.EstimateAsync("openai", "profile-model", [
            Interaction(1, "requested-model", "reported-model", new TokenUsage(10, 2, 12)),
            Interaction(2, "requested-model", "reported-model", new TokenUsage(20, 4, 24))
        ]);

        Assert.Equal(0.00009m, result.EstimatedCost!.Amount);
        Assert.True(result.EstimatedCost.Complete);
        Assert.Equal(2, result.EstimatedCost.PricedInteractions);
        Assert.Equal(2, result.EstimatedCost.TotalInteractions);
        Assert.All(result.Interactions, interaction =>
            Assert.Equal("reported-model", interaction.ModelUsedForPricing));
    }

    [Fact]
    public async Task AI_COST_003_RequestModelIsUsedWhenProviderDoesNotReportOne()
    {
        var result = await Service(Quote("anthropic", "requested-model", 5m, 20m))
            .EstimateAsync("anthropic", "profile-model", [
                new AiProviderInteractionUsage(1, "anthropic", "requested-model", null, null,
                    "req-1", new TokenUsage(1_000, 100, 1_100))
            ]);

        Assert.Equal("requested-model", result.Interactions.Single().ModelUsedForPricing);
        Assert.Equal(0.007m, result.EstimatedCost!.Amount);
    }

    [Fact]
    public async Task AI_COST_004_UnknownPricingAndMissingUsageProducePartialNotZero()
    {
        var result = await Service(Quote("openai", "known", 1m, 2m)).EstimateAsync(
            "openai", "profile-model", [
                Interaction(1, "known", "known", new TokenUsage(100, 100, 200)),
                Interaction(2, "unknown", "unknown", new TokenUsage(100, 100, 200)),
                Interaction(3, "known", "known", null)
            ]);

        Assert.Equal(0.0003m, result.EstimatedCost!.Amount);
        Assert.False(result.EstimatedCost.Complete);
        Assert.Equal(1, result.EstimatedCost.PricedInteractions);
        Assert.Equal(3, result.EstimatedCost.TotalInteractions);
        Assert.Null(result.Interactions[1].EstimatedTotalCost);
        Assert.Null(result.Interactions[2].EstimatedTotalCost);
    }

    [Fact]
    public async Task AI_COST_005_UnsupportedProviderIsUnavailableWithoutFailing()
    {
        var result = await Service().EstimateAsync("future-provider", "future-model", [
            new AiProviderInteractionUsage(1, "future-provider", "future-model", null, null,
                "req-1", new TokenUsage(100, 100, 200))
        ]);

        Assert.Null(result.EstimatedCost);
        Assert.Single(result.Interactions);
    }

    [Fact]
    public void AI_COST_006_RunAggregationRetainsPartialCoverage()
    {
        var result = EstimatedCostAggregation.Sum([
            new EstimatedCost(0.01m, "USD"),
            new EstimatedCost(0.02m, "USD") { Complete = false, PricedInteractions = 1, TotalInteractions = 2 },
            null
        ], [1, 2, 3]);

        Assert.Equal(0.03m, result!.Amount);
        Assert.False(result.Complete);
        Assert.Equal(2, result.PricedInteractions);
        Assert.Equal(6, result.TotalInteractions);
    }

    private static AiCostAccountingService Service(params AiModelPricingQuote[] quotes)
    {
        var source = new Source(quotes);
        var pricing = new AiModelPricingService(new Repository(), [source], new Clock(),
            new AiModelPricingOptions());
        return new AiCostAccountingService(pricing, new CostEstimator(), new NullAiCostAccountingLog());
    }

    private static AiProviderInteractionUsage Interaction(
        int attempt, string requestedModel, string? reportedModel, TokenUsage? usage) =>
        new(attempt, "openai", requestedModel, "openai", reportedModel, $"req-{attempt}", usage);

    private static AiModelPricingQuote Quote(
        string provider, string model, decimal input, decimal output) =>
        new(provider, model, input, null, output, "USD", "Test catalog",
            new Uri("https://example.test/pricing"), "test-v1", Now,
            AiModelPricingSourceKind.BundledCatalog);

    private static AiModelPricingRecord Record(
        string provider, string model, decimal input, decimal output) =>
        new(provider, model, input, null, output, "USD", "Test catalog",
            new Uri("https://example.test/pricing"), "test-v1", Now, Now, Now.AddDays(7),
            AiModelPricingSourceKind.BundledCatalog);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Source(IEnumerable<AiModelPricingQuote> quotes) : IAiModelPricingSource
    {
        private readonly IReadOnlyDictionary<string, AiModelPricingQuote> values = quotes.ToDictionary(
            quote => $"{quote.Provider}\n{quote.ModelId}", StringComparer.Ordinal);
        public int Precedence => 100;
        public Task<AiModelPricingQuote?> ResolveAsync(string provider, string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault($"{provider}\n{modelId}"));
    }

    private sealed class Repository : IAiModelPricingRepository
    {
        private readonly Dictionary<string, AiModelPricingRecord> values = new(StringComparer.Ordinal);
        public Task<AiModelPricingRecord?> GetAsync(string provider, string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault($"{provider}\n{modelId}"));
        public Task UpsertResolvedAsync(AiModelPricingRecord pricing,
            CancellationToken cancellationToken = default)
        {
            values[$"{pricing.Provider}\n{pricing.ModelId}"] = pricing;
            return Task.CompletedTask;
        }
    }
}

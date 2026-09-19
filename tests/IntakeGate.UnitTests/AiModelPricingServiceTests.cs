using IntakeGate.Application.AiPricing;
using IntakeGate.Application.Time;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class AiModelPricingServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    [Fact]
    public async Task PricingCatalogHitIsPersistedWithExactSevenDayFreshness()
    {
        var repository = new MemoryRepository();
        var source = new FakeSource(Quote("openai", "gpt-tested", 2m, 0.2m, 8m));
        var service = Create(repository, source);

        var result = await service.GetAsync("OpenAI", "gpt-tested");

        Assert.True(result.Available);
        Assert.False(result.Stale);
        Assert.Equal(2m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(0.2m, result.Pricing.CachedInputPerMillionTokens);
        Assert.Equal(Now.AddDays(7), result.Pricing.ExpiresAtUtc);
        Assert.Equal(1, source.Calls);
        Assert.NotNull(await repository.GetAsync("openai", "gpt-tested"));
    }

    [Fact]
    public async Task UnknownModelIsUnavailableWithoutInventingZeroPricing()
    {
        var service = Create(new MemoryRepository(), new FakeSource(null));

        var result = await service.GetAsync("anthropic", "unknown-model");

        Assert.False(result.Available);
        Assert.Null(result.Pricing);
        Assert.False(result.RefreshFailed);
    }

    [Fact]
    public async Task FreshStoredPricingAvoidsAnotherSourceCall()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("openai", "gpt-tested", Now.AddDays(-1), Now.AddDays(6)));
        var source = new FakeSource(Quote("openai", "gpt-tested", 99m, null, 99m));
        var service = Create(repository, source);

        var result = await service.GetAsync("openai", "gpt-tested");

        Assert.Equal(1m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task StaleStoredPricingRefreshesFromSource()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("openai", "gpt-tested", Now.AddDays(-8), Now.AddDays(-1)));
        var source = new FakeSource(Quote("openai", "gpt-tested", 3m, null, 9m));
        var service = Create(repository, source);

        var result = await service.GetAsync("openai", "gpt-tested");

        Assert.False(result.Stale);
        Assert.Equal(3m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ForcedRefreshBypassesFreshnessAndReplacesOnlyAfterSuccess()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("openai", "gpt-tested", Now.AddDays(-1), Now.AddDays(6)));
        var source = new FakeSource(Quote("openai", "gpt-tested", 4m, 0.4m, 12m));
        var service = Create(repository, source);

        var result = await service.GetAsync("openai", "gpt-tested", forceRefresh: true);

        Assert.Equal(4m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(Now, result.Pricing.VerifiedAtUtc);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task RefreshFailurePreservesLastKnownPricingAndMarksItStale()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("anthropic", "claude-tested", Now.AddDays(-8), Now.AddDays(-1)));
        var source = new FakeSource(null) { Throw = true };
        var service = Create(repository, source);

        var result = await service.GetAsync("anthropic", "claude-tested", forceRefresh: true);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.True(result.RefreshFailed);
        Assert.Equal(1m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(Now.AddDays(-8), result.Pricing.VerifiedAtUtc);
    }

    [Fact]
    public async Task InvalidSourceQuotePreservesLastKnownPricingAndMarksRefreshFailed()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("openai", "gpt-tested", Now.AddDays(-8), Now.AddDays(-1)));
        var source = new FakeSource(Quote("different-provider", "gpt-tested", 3m, null, 9m));
        var service = Create(repository, source);

        var result = await service.GetAsync("openai", "gpt-tested", forceRefresh: true);

        Assert.True(result.Available);
        Assert.True(result.Stale);
        Assert.True(result.RefreshFailed);
        Assert.Equal(1m, result.Pricing!.InputPerMillionTokens);
        Assert.Equal(Now.AddDays(-8), result.Pricing.VerifiedAtUtc);
    }

    [Fact]
    public async Task CachedInputCanBeUnavailableAndProviderModelKeysRemainIsolated()
    {
        var repository = new MemoryRepository();
        var source = new FakeSource(null)
        {
            Quotes =
            {
                ["openai\nsame-id"] = Quote("openai", "same-id", 1m, null, 2m),
                ["anthropic\nsame-id"] = Quote("anthropic", "same-id", 5m, 0.5m, 10m)
            }
        };
        var service = Create(repository, source);

        var openAi = await service.GetAsync("openai", "same-id");
        var anthropic = await service.GetAsync("anthropic", "same-id");

        Assert.Null(openAi.Pricing!.CachedInputPerMillionTokens);
        Assert.Equal(0.5m, anthropic.Pricing!.CachedInputPerMillionTokens);
        Assert.Equal(1m, openAi.Pricing.InputPerMillionTokens);
        Assert.Equal(5m, anthropic.Pricing.InputPerMillionTokens);
    }

    [Fact]
    public async Task ManualOverrideHasPrecedenceAndIsNeverReplacedByRefresh()
    {
        var repository = new MemoryRepository();
        repository.Seed(Record("openai", "enterprise-model", Now.AddYears(-1), null,
            AiModelPricingSourceKind.ManualOverride));
        var source = new FakeSource(Quote("openai", "enterprise-model", 99m, null, 99m));
        var service = Create(repository, source);

        var result = await service.GetAsync("openai", "enterprise-model", forceRefresh: true);

        Assert.Equal(AiModelPricingSourceKind.ManualOverride, result.Pricing!.SourceKind);
        Assert.Equal(0, source.Calls);
        Assert.False(result.Stale);
    }

    private static AiModelPricingService Create(MemoryRepository repository, FakeSource source) =>
        new(repository, [source], new FixedClock(Now), new AiModelPricingOptions());

    private static AiModelPricingQuote Quote(
        string provider, string model, decimal input, decimal? cached, decimal output) =>
        new(provider, model, input, cached, output, "USD", "Test catalog",
            new Uri("https://example.test/pricing"), "test-v1", Now.AddDays(-1),
            AiModelPricingSourceKind.BundledCatalog);

    private static AiModelPricingRecord Record(
        string provider, string model, DateTimeOffset verified, DateTimeOffset? expires,
        AiModelPricingSourceKind kind = AiModelPricingSourceKind.BundledCatalog) =>
        new(provider, model, 1m, null, 2m, "USD", "Stored catalog",
            new Uri("https://example.test/pricing"), "stored-v1", Now.AddDays(-10),
            verified, expires, kind);

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class FakeSource(AiModelPricingQuote? fallback) : IAiModelPricingSource
    {
        public int Precedence => 100;
        public int Calls { get; private set; }
        public bool Throw { get; init; }
        public Dictionary<string, AiModelPricingQuote> Quotes { get; } = new(StringComparer.Ordinal);

        public Task<AiModelPricingQuote?> ResolveAsync(
            string provider, string modelId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("Synthetic source outage.");
            return Task.FromResult(Quotes.GetValueOrDefault($"{provider}\n{modelId}") ?? fallback);
        }
    }

    private sealed class MemoryRepository : IAiModelPricingRepository
    {
        private readonly Dictionary<string, AiModelPricingRecord> values = new(StringComparer.Ordinal);

        public void Seed(AiModelPricingRecord value) => values[Key(value.Provider, value.ModelId)] = value;

        public Task<AiModelPricingRecord?> GetAsync(
            string provider, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(Key(provider, modelId)));

        public Task UpsertResolvedAsync(
            AiModelPricingRecord pricing, CancellationToken cancellationToken = default)
        {
            var key = Key(pricing.Provider, pricing.ModelId);
            if (values.GetValueOrDefault(key)?.SourceKind != AiModelPricingSourceKind.ManualOverride)
                values[key] = pricing;
            return Task.CompletedTask;
        }

        private static string Key(string provider, string modelId) => $"{provider}\n{modelId}";
    }
}

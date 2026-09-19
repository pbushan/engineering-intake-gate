using System.Collections.Concurrent;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.AiPricing;

public sealed class AiModelPricingService
{
    private readonly IAiModelPricingRepository repository;
    private readonly IReadOnlyList<IAiModelPricingSource> sources;
    private readonly IClock clock;
    private readonly TimeSpan freshnessTtl;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> resolutionLocks = new(StringComparer.Ordinal);

    public AiModelPricingService(
        IAiModelPricingRepository repository,
        IEnumerable<IAiModelPricingSource> sources,
        IClock clock,
        AiModelPricingOptions options)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(sources);
        this.sources = sources.OrderByDescending(source => source.Precedence).ToArray();
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        if (options.FreshnessTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Pricing freshness TTL must be greater than zero.");
        freshnessTtl = options.FreshnessTtl;
    }

    public async Task<AiModelPricingLookupResult> GetAsync(
        string provider,
        string modelId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!AiProviderNames.TryNormalize(provider, out var normalizedProvider))
            throw new ArgumentException("Unsupported AI provider.", nameof(provider));
        var normalizedModel = modelId?.Trim() ?? string.Empty;
        if (normalizedModel.Length is 0 or > 200)
            throw new ArgumentException("A valid model ID is required.", nameof(modelId));

        var key = $"{normalizedProvider}\n{normalizedModel}";
        var gate = resolutionLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveLockedAsync(normalizedProvider, normalizedModel, forceRefresh, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AiModelPricingLookupResult> ResolveLockedAsync(
        string provider,
        string modelId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var existing = await repository.GetAsync(provider, modelId, cancellationToken);

        // Explicit negotiated pricing is authoritative until an administrator replaces it.
        if (existing?.SourceKind == AiModelPricingSourceKind.ManualOverride)
            return new(existing, false, false);

        if (!forceRefresh && existing is not null && !IsStale(existing, now))
            return new(existing, false, false);

        var sourceFailed = false;
        foreach (var source in sources)
        {
            AiModelPricingQuote? quote;
            try
            {
                quote = await source.ResolveAsync(provider, modelId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                sourceFailed = true;
                continue;
            }

            if (quote is null) continue;
            try
            {
                ValidateQuote(quote, provider, modelId);
            }
            catch (InvalidOperationException)
            {
                sourceFailed = true;
                continue;
            }
            var refreshed = new AiModelPricingRecord(
                provider,
                modelId,
                quote.InputPerMillionTokens,
                quote.CachedInputPerMillionTokens,
                quote.OutputPerMillionTokens,
                quote.Currency,
                quote.Source,
                quote.SourceUri,
                quote.CatalogVersion,
                quote.EffectiveAtUtc?.ToUniversalTime(),
                now,
                now + freshnessTtl,
                quote.SourceKind);
            await repository.UpsertResolvedAsync(refreshed, cancellationToken);
            var persisted = await repository.GetAsync(provider, modelId, cancellationToken);
            return new(persisted ?? refreshed, false, false, true);
        }

        if (existing is not null)
            return new(existing, true, true);
        return new(null, false, sourceFailed);
    }

    private static bool IsStale(AiModelPricingRecord pricing, DateTimeOffset now) =>
        pricing.ExpiresAtUtc is not { } expiry || expiry <= now;

    private static void ValidateQuote(AiModelPricingQuote quote, string provider, string modelId)
    {
        if (!string.Equals(quote.Provider, provider, StringComparison.Ordinal) ||
            !string.Equals(quote.ModelId, modelId, StringComparison.Ordinal) ||
            quote.InputPerMillionTokens < 0 || quote.OutputPerMillionTokens < 0 ||
            quote.CachedInputPerMillionTokens is < 0 ||
            string.IsNullOrWhiteSpace(quote.Currency) ||
            string.IsNullOrWhiteSpace(quote.Source) ||
            string.IsNullOrWhiteSpace(quote.CatalogVersion))
        {
            throw new InvalidOperationException("The pricing source returned an invalid quote.");
        }
    }
}

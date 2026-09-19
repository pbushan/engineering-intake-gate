using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.AiPricing;

namespace IntakeGate.Infrastructure.Ai.Pricing;

public sealed class BundledAiModelPricingCatalog : IAiModelPricingSource
{
    private const string ResourceName =
        "IntakeGate.Infrastructure.Ai.Pricing.ai-model-pricing-catalog.v1.json";
    private readonly Lazy<IReadOnlyDictionary<string, AiModelPricingQuote>> entries = new(Load);

    public int Precedence => 100;

    public Task<AiModelPricingQuote?> ResolveAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries.Value.TryGetValue(Key(provider, modelId), out var quote);
        return Task.FromResult(quote);
    }

    private static IReadOnlyDictionary<string, AiModelPricingQuote> Load()
    {
        using var stream = typeof(BundledAiModelPricingCatalog).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled AI model pricing catalog is missing.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, options)
            ?? throw new InvalidOperationException("The bundled AI model pricing catalog is empty.");
        if (string.IsNullOrWhiteSpace(catalog.Version) || catalog.Entries.Count == 0)
            throw new InvalidOperationException("The bundled AI model pricing catalog is invalid.");

        var effectiveAt = catalog.PublishedAtUtc.ToUniversalTime();
        var result = new Dictionary<string, AiModelPricingQuote>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            var uri = new Uri(entry.SourceUri, UriKind.Absolute);
            var quote = new AiModelPricingQuote(
                entry.Provider,
                entry.ModelId,
                entry.InputPerMillionTokens,
                entry.CachedInputPerMillionTokens,
                entry.OutputPerMillionTokens,
                entry.Currency,
                entry.Source,
                uri,
                catalog.Version,
                effectiveAt,
                AiModelPricingSourceKind.BundledCatalog);
            if (!result.TryAdd(Key(entry.Provider, entry.ModelId), quote))
                throw new InvalidOperationException("The bundled AI model pricing catalog contains a duplicate key.");
        }
        return result;
    }

    private static string Key(string provider, string modelId) => $"{provider}\n{modelId}";

    private sealed record Catalog(string Version, DateTimeOffset PublishedAtUtc, IReadOnlyList<CatalogEntry> Entries);

    private sealed record CatalogEntry(
        string Provider,
        string ModelId,
        decimal InputPerMillionTokens,
        decimal? CachedInputPerMillionTokens,
        decimal OutputPerMillionTokens,
        string Currency,
        string Source,
        string SourceUri);
}

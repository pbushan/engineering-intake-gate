namespace IntakeGate.Application.AiPricing;

public enum AiModelPricingSourceKind
{
    BundledCatalog,
    AuthoritativeCatalog,
    ManualOverride
}

public sealed record AiModelPricingQuote(
    string Provider,
    string ModelId,
    decimal InputPerMillionTokens,
    decimal? CachedInputPerMillionTokens,
    decimal OutputPerMillionTokens,
    string Currency,
    string Source,
    Uri? SourceUri,
    string CatalogVersion,
    DateTimeOffset? EffectiveAtUtc,
    AiModelPricingSourceKind SourceKind);

public sealed record AiModelPricingRecord(
    string Provider,
    string ModelId,
    decimal InputPerMillionTokens,
    decimal? CachedInputPerMillionTokens,
    decimal OutputPerMillionTokens,
    string Currency,
    string Source,
    Uri? SourceUri,
    string CatalogVersion,
    DateTimeOffset? EffectiveAtUtc,
    DateTimeOffset VerifiedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    AiModelPricingSourceKind SourceKind);

public sealed record AiModelPricingLookupResult(
    AiModelPricingRecord? Pricing,
    bool Stale,
    bool RefreshFailed)
{
    public bool Available => Pricing is not null;
}

/// <summary>
/// Provider-neutral source boundary. Sources are ordered by precedence so a future
/// authoritative feed can be inserted ahead of the bundled fallback without changing callers.
/// </summary>
public interface IAiModelPricingSource
{
    int Precedence { get; }

    Task<AiModelPricingQuote?> ResolveAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default);
}

public interface IAiModelPricingRepository
{
    Task<AiModelPricingRecord?> GetAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default);

    Task UpsertResolvedAsync(
        AiModelPricingRecord pricing,
        CancellationToken cancellationToken = default);
}

public sealed record AiModelPricingOptions
{
    public static readonly TimeSpan DefaultFreshnessTtl = TimeSpan.FromDays(7);

    public TimeSpan FreshnessTtl { get; init; } = DefaultFreshnessTtl;
}

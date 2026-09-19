using IntakeGate.Application.AiPricing;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Application.Audit;

public sealed record EstimatedCostBreakdown(
    decimal InputAmount,
    decimal OutputAmount,
    decimal TotalAmount,
    string Currency);

/// <summary>Pure fixed-point token arithmetic over an already resolved pricing snapshot.</summary>
public sealed class CostEstimator
{
    public EstimatedCostBreakdown Estimate(AiModelPricingRecord pricing, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.TotalTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Token usage cannot be negative.");

        var input = usage.InputTokens / 1_000_000m * pricing.InputPerMillionTokens;
        var output = usage.OutputTokens / 1_000_000m * pricing.OutputPerMillionTokens;
        return new EstimatedCostBreakdown(input, output, input + output, pricing.Currency);
    }
}

public sealed record AiCostAccountingResult(
    EstimatedCost? EstimatedCost,
    IReadOnlyList<AiInteractionCostRecord> Interactions);

public interface IAiCostAccountingService
{
    Task<AiCostAccountingResult> EstimateAsync(
        string configuredProvider,
        string configuredModel,
        IReadOnlyList<AiProviderInteractionUsage> interactions,
        CancellationToken cancellationToken = default);
}

public interface IAiCostAccountingLog
{
    void PricingResolved(string provider, string model, bool refreshed, bool stale);
    void PricingUnavailable(string provider, string model, bool refreshFailed);
    void MissingUsage(string provider, string model, int attempt);
    void CalculationFailed(string provider, string model, int attempt, string category);
}

public sealed class NullAiCostAccountingLog : IAiCostAccountingLog
{
    public void PricingResolved(string provider, string model, bool refreshed, bool stale) { }
    public void PricingUnavailable(string provider, string model, bool refreshFailed) { }
    public void MissingUsage(string provider, string model, int attempt) { }
    public void CalculationFailed(string provider, string model, int attempt, string category) { }
}

public sealed class UnavailableAiCostAccountingService : IAiCostAccountingService
{
    public Task<AiCostAccountingResult> EstimateAsync(
        string configuredProvider,
        string configuredModel,
        IReadOnlyList<AiProviderInteractionUsage> interactions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AiCostAccountingResult(null, interactions.Select(interaction =>
            new AiInteractionCostRecord(
                interaction.Attempt,
                interaction.RequestedProviderIdentifier,
                interaction.RequestedModelIdentifier,
                string.IsNullOrWhiteSpace(interaction.ActualProviderIdentifier)
                    ? string.IsNullOrWhiteSpace(interaction.RequestedProviderIdentifier)
                        ? configuredProvider : interaction.RequestedProviderIdentifier
                    : interaction.ActualProviderIdentifier,
                !string.IsNullOrWhiteSpace(interaction.ProviderReportedModel)
                    ? interaction.ProviderReportedModel
                    : string.IsNullOrWhiteSpace(interaction.RequestedModelIdentifier)
                        ? configuredModel : interaction.RequestedModelIdentifier,
                interaction.ProviderRequestId,
                interaction.TokenUsage,
                null,
                null,
                null,
                null)).ToArray()));
    }
}

/// <summary>
/// Resolves the same cached pricing used by Setup and snapshots a cost for every provider
/// interaction. Pricing failures are represented as unavailable records and never escape into
/// the evaluation outcome.
/// </summary>
public sealed class AiCostAccountingService(
    AiModelPricingService pricingService,
    CostEstimator estimator,
    IAiCostAccountingLog log) : IAiCostAccountingService
{
    public async Task<AiCostAccountingResult> EstimateAsync(
        string configuredProvider,
        string configuredModel,
        IReadOnlyList<AiProviderInteractionUsage> interactions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactions);
        var records = new List<AiInteractionCostRecord>(interactions.Count);
        foreach (var interaction in interactions)
        {
            var provider = First(interaction.ActualProviderIdentifier,
                interaction.RequestedProviderIdentifier, configuredProvider);
            var model = First(interaction.ProviderReportedModel,
                interaction.RequestedModelIdentifier, configuredModel);

            if (interaction.TokenUsage is null)
            {
                log.MissingUsage(provider, model, interaction.Attempt);
                records.Add(Unavailable(interaction, provider, model));
                continue;
            }

            try
            {
                var lookup = await pricingService.GetAsync(provider, model,
                    forceRefresh: false, cancellationToken);
                if (lookup.Pricing is null ||
                    !string.Equals(lookup.Pricing.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    log.PricingUnavailable(provider, model, lookup.RefreshFailed);
                    records.Add(Unavailable(interaction, provider, model));
                    continue;
                }

                var breakdown = estimator.Estimate(lookup.Pricing, interaction.TokenUsage);
                var snapshot = new AppliedAiPricing(
                    lookup.Pricing.InputPerMillionTokens,
                    lookup.Pricing.OutputPerMillionTokens,
                    lookup.Pricing.Currency.ToUpperInvariant(),
                    lookup.Pricing.Source,
                    lookup.Pricing.SourceUri,
                    lookup.Pricing.CatalogVersion,
                    lookup.Pricing.EffectiveAtUtc,
                    lookup.Pricing.VerifiedAtUtc,
                    lookup.Pricing.SourceKind,
                    lookup.Stale);
                records.Add(new AiInteractionCostRecord(
                    interaction.Attempt,
                    interaction.RequestedProviderIdentifier,
                    interaction.RequestedModelIdentifier,
                    provider,
                    model,
                    interaction.ProviderRequestId,
                    interaction.TokenUsage,
                    breakdown.InputAmount,
                    breakdown.OutputAmount,
                    breakdown.TotalAmount,
                    snapshot));
                log.PricingResolved(provider, model, lookup.Refreshed, lookup.Stale);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                log.CalculationFailed(provider, model, interaction.Attempt,
                    exception.GetType().Name);
                records.Add(Unavailable(interaction, provider, model));
            }
        }

        return new AiCostAccountingResult(Aggregate(records), records);
    }

    private static EstimatedCost? Aggregate(IReadOnlyList<AiInteractionCostRecord> records)
    {
        var priced = records.Where(record => record.EstimatedTotalCost is not null &&
            string.Equals(record.Pricing?.Currency, "USD", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (priced.Length == 0) return null;
        var identities = priced.Select(record => record.Pricing!.CatalogVersion)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new EstimatedCost(priced.Sum(record => record.EstimatedTotalCost!.Value), "USD")
        {
            Complete = priced.Length == records.Count,
            PricedInteractions = priced.Length,
            TotalInteractions = records.Count,
            PricingIdentity = identities.Length == 1 ? identities[0] : "multiple-pricing-snapshots"
        };
    }

    private static AiInteractionCostRecord Unavailable(
        AiProviderInteractionUsage interaction,
        string provider,
        string model) => new(
            interaction.Attempt,
            interaction.RequestedProviderIdentifier,
            interaction.RequestedModelIdentifier,
            provider,
            model,
            interaction.ProviderRequestId,
            interaction.TokenUsage,
            null,
            null,
            null,
            null);

    private static string First(params string?[] candidates) =>
        candidates.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}

public static class EstimatedCostAggregation
{
    public static EstimatedCost? Sum(
        IReadOnlyList<EstimatedCost?> costs,
        IReadOnlyList<int>? interactionCounts = null)
    {
        if (interactionCounts is not null && interactionCounts.Count != costs.Count)
            throw new ArgumentException("Interaction counts must align with costs.", nameof(interactionCounts));

        var known = costs.OfType<EstimatedCost>().ToArray();
        if (known.Length == 0) return null;
        var currencies = known.Select(cost => cost.Currency.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (currencies.Length != 1) return null;
        var identities = known.Select(cost => cost.PricingIdentity)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new EstimatedCost(known.Sum(cost => cost.Amount), currencies[0])
        {
            Complete = known.Length == costs.Count && known.All(cost => cost.Complete),
            PricedInteractions = known.Sum(cost => cost.PricedInteractions),
            TotalInteractions = costs.Select((cost, index) => cost?.TotalInteractions ??
                Math.Max(1, interactionCounts?[index] ?? 1)).Sum(),
            PricingIdentity = identities.Length == 1 ? identities[0] : "multiple-pricing-snapshots"
        };
    }
}

using IntakeGate.Application.AiPricing;
using IntakeGate.Infrastructure.Ai.Pricing;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AiModelPricingPersistenceTests : IDisposable
{
    private readonly string database = Path.Combine(
        Path.GetTempPath(), $"intake-gate-pricing-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task CatalogUsesReviewedProviderModelKeysAndNullableCachedInput()
    {
        var catalog = new BundledAiModelPricingCatalog();

        var openAi = await catalog.ResolveAsync("openai", "gpt-5.6-sol");
        var anthropic = await catalog.ResolveAsync("anthropic", "claude-sonnet-5");
        var unavailable = await catalog.ResolveAsync("openai", "not-in-catalog");

        Assert.Equal((4m, 0.4m, 20m, "USD"),
            (openAi!.InputPerMillionTokens, openAi.CachedInputPerMillionTokens,
                openAi.OutputPerMillionTokens, openAi.Currency));
        Assert.Equal((2m, 0.2m, 10m),
            (anthropic!.InputPerMillionTokens, anthropic.CachedInputPerMillionTokens,
                anthropic.OutputPerMillionTokens));
        Assert.Null(unavailable);
    }

    [Fact]
    public async Task RepositoryRoundTripsPricingAndDoesNotOverwriteManualOverride()
    {
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var repository = new SqliteAiModelPricingRepository(database);
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        var manual = Record(AiModelPricingSourceKind.ManualOverride, now, null);
        await InsertDirectAsync(manual);

        await repository.UpsertResolvedAsync(Record(
            AiModelPricingSourceKind.BundledCatalog, now.AddDays(1), now.AddDays(8)));

        var persisted = await repository.GetAsync("openai", "enterprise-model");
        Assert.NotNull(persisted);
        Assert.Equal(AiModelPricingSourceKind.ManualOverride, persisted.SourceKind);
        Assert.Equal(1.25m, persisted.InputPerMillionTokens);
        Assert.Null(persisted.CachedInputPerMillionTokens);
        Assert.Null(persisted.ExpiresAtUtc);
    }

    private static AiModelPricingRecord Record(
        AiModelPricingSourceKind kind, DateTimeOffset verified, DateTimeOffset? expires) =>
        new("openai", "enterprise-model", 1.25m, null, 7.5m, "USD", "Negotiated contract",
            null, "contract-2026", verified, verified, expires, kind);

    private async Task InsertDirectAsync(AiModelPricingRecord pricing)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_model_pricing_cache VALUES
                ($provider, $model, '1.25', NULL, '7.5', 'USD', 'Negotiated contract', NULL,
                 'contract-2026', $effective, $verified, NULL, 'ManualOverride', $verified);
            """;
        command.Parameters.AddWithValue("$provider", pricing.Provider);
        command.Parameters.AddWithValue("$model", pricing.ModelId);
        command.Parameters.AddWithValue("$effective", pricing.EffectiveAtUtc!.Value.ToString("O"));
        command.Parameters.AddWithValue("$verified", pricing.VerifiedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { database, database + "-shm", database + "-wal" })
            File.Delete(path);
    }
}

using System.Globalization;
using IntakeGate.Application.AiPricing;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteAiModelPricingRepository : IAiModelPricingRepository
{
    private readonly string connectionString;

    public SqliteAiModelPricingRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task<AiModelPricingRecord?> GetAsync(
        string provider,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_per_million_tokens, cached_input_per_million_tokens,
                   output_per_million_tokens, currency, source, source_uri,
                   catalog_version, effective_at_utc, verified_at_utc,
                   expires_at_utc, source_kind
            FROM ai_model_pricing_cache
            WHERE provider = $provider AND model_id = $model;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$model", modelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new AiModelPricingRecord(
            provider,
            modelId,
            ParseDecimal(reader.GetString(0)),
            reader.IsDBNull(1) ? null : ParseDecimal(reader.GetString(1)),
            ParseDecimal(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : new Uri(reader.GetString(5), UriKind.Absolute),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            ParseUtc(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
            Enum.Parse<AiModelPricingSourceKind>(reader.GetString(10), ignoreCase: false));
    }

    public async Task UpsertResolvedAsync(
        AiModelPricingRecord pricing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_model_pricing_cache
                (provider, model_id, input_per_million_tokens, cached_input_per_million_tokens,
                 output_per_million_tokens, currency, source, source_uri, catalog_version,
                 effective_at_utc, verified_at_utc, expires_at_utc, source_kind, updated_at_utc)
            VALUES
                ($provider, $model, $input, $cachedInput, $output, $currency, $source,
                 $sourceUri, $catalogVersion, $effective, $verified, $expires, $sourceKind, $verified)
            ON CONFLICT(provider, model_id) DO UPDATE SET
                input_per_million_tokens = excluded.input_per_million_tokens,
                cached_input_per_million_tokens = excluded.cached_input_per_million_tokens,
                output_per_million_tokens = excluded.output_per_million_tokens,
                currency = excluded.currency,
                source = excluded.source,
                source_uri = excluded.source_uri,
                catalog_version = excluded.catalog_version,
                effective_at_utc = excluded.effective_at_utc,
                verified_at_utc = excluded.verified_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                source_kind = excluded.source_kind,
                updated_at_utc = excluded.updated_at_utc
            WHERE ai_model_pricing_cache.source_kind <> 'ManualOverride';
            """;
        command.Parameters.AddWithValue("$provider", pricing.Provider);
        command.Parameters.AddWithValue("$model", pricing.ModelId);
        command.Parameters.AddWithValue("$input", FormatDecimal(pricing.InputPerMillionTokens));
        command.Parameters.AddWithValue("$cachedInput", pricing.CachedInputPerMillionTokens is { } cached
            ? FormatDecimal(cached) : DBNull.Value);
        command.Parameters.AddWithValue("$output", FormatDecimal(pricing.OutputPerMillionTokens));
        command.Parameters.AddWithValue("$currency", pricing.Currency);
        command.Parameters.AddWithValue("$source", pricing.Source);
        command.Parameters.AddWithValue("$sourceUri", pricing.SourceUri?.AbsoluteUri ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$catalogVersion", pricing.CatalogVersion);
        command.Parameters.AddWithValue("$effective", pricing.EffectiveAtUtc is { } effective
            ? FormatUtc(effective) : DBNull.Value);
        command.Parameters.AddWithValue("$verified", FormatUtc(pricing.VerifiedAtUtc));
        command.Parameters.AddWithValue("$expires", pricing.ExpiresAtUtc is { } expires
            ? FormatUtc(expires) : DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", pricing.SourceKind.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
}

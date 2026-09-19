using System.Globalization;
using System.Text.Json;
using IntakeGate.Application.Audit;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

/// <summary>Bounded persisted-audit read with fixed-point aggregation in application code.</summary>
public sealed class SqliteHomeSummaryReader : IHomeSummaryReader
{
    private readonly string connectionString;

    public SqliteHomeSummaryReader(string databasePath)
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

    public async Task<HomeSummaryAggregate> GetAsync(
        DateTimeOffset windowStartInclusiveUtc,
        DateTimeOffset windowEndExclusiveUtc,
        CancellationToken cancellationToken = default)
    {
        var start = windowStartInclusiveUtc.ToUniversalTime();
        var end = windowEndExclusiveUtc.ToUniversalTime();
        if (start >= end) throw new ArgumentOutOfRangeException(nameof(windowStartInclusiveUtc));

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM evaluation_audits
            WHERE evaluated_at_utc >= $start AND evaluated_at_utc < $end
            ORDER BY evaluated_at_utc, evaluation_id;
            """;
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var evaluated = 0;
        var ready = 0;
        var incomplete = 0;
        var errors = 0;
        var notEligible = 0;
        var skipped = 0;
        var suppressed = 0;
        var withCost = 0;
        var withCompleteCost = 0;
        var withPartialCost = 0;
        var withoutCost = 0;
        var amounts = new List<(decimal Amount, string Currency)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            var payload = document.RootElement;
            var outcome = Classify(payload);
            if (Boolean(payload, "updateSuppressed")) suppressed++;
            var relevant = outcome is "pass" or "fail" or "error";
            switch (outcome)
            {
                case "pass": ready++; break;
                case "fail": incomplete++; break;
                case "error": errors++; break;
                case "not_eligible": notEligible++; break;
                default: skipped++; break;
            }
            if (!relevant) continue;
            evaluated++;

            if (!payload.TryGetProperty("estimatedCost", out var estimatedCost) ||
                estimatedCost.ValueKind != JsonValueKind.Object ||
                !estimatedCost.TryGetProperty("amount", out var amountElement) ||
                !amountElement.TryGetDecimal(out var amount) ||
                String(estimatedCost, "currency") is not { Length: > 0 } currency)
            {
                withoutCost++;
                continue;
            }

            withCost++;
            var complete = !estimatedCost.TryGetProperty("complete", out var completeElement) ||
                completeElement.ValueKind == JsonValueKind.True;
            if (complete) withCompleteCost++; else withPartialCost++;
            amounts.Add((amount, currency.ToUpperInvariant()));
        }

        var currencies = amounts.Select(value => value.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var aggregateComplete = withoutCost == 0 && withPartialCost == 0 && currencies.Length <= 1;
        EstimatedCost? cost = currencies.Length == 1
            ? new EstimatedCost(amounts.Sum(value => value.Amount), currencies[0])
            {
                Complete = aggregateComplete,
                PricedInteractions = withCost,
                TotalInteractions = evaluated
            }
            : null;

        return new HomeSummaryAggregate(
            start, end, evaluated, ready, incomplete, errors, notEligible, skipped, suppressed,
            cost, withCost, withCompleteCost, withPartialCost, withoutCost, aggregateComplete);
    }

    private static string Classify(JsonElement payload)
    {
        var eligibility = String(payload, "eligibility");
        var processingStatus = String(payload, "processingStatus");
        var decision = String(payload, "decision");
        if (string.Equals(eligibility, "notEligible", StringComparison.OrdinalIgnoreCase))
            return "not_eligible";
        if (string.Equals(processingStatus, "error", StringComparison.OrdinalIgnoreCase))
            return "error";
        if (eligibility is null || string.Equals(eligibility, "eligible", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(decision, "pass", StringComparison.OrdinalIgnoreCase)) return "pass";
            if (string.Equals(decision, "fail", StringComparison.OrdinalIgnoreCase)) return "fail";
        }
        return "skipped";
    }

    private static string? String(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool Boolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

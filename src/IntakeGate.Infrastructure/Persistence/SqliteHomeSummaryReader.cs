using System.Globalization;
using IntakeGate.Application.Audit;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

/// <summary>Single-row SQL aggregate over the persisted assessment audit window.</summary>
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
            WITH classified AS (
                SELECT
                    CASE
                        WHEN lower(COALESCE(json_extract(payload_json, '$.eligibility'), '')) = 'noteligible'
                            THEN 'not_eligible'
                        WHEN lower(COALESCE(json_extract(payload_json, '$.processingStatus'), '')) = 'error'
                            THEN 'error'
                        WHEN lower(COALESCE(json_extract(payload_json, '$.eligibility'), 'eligible')) = 'eligible'
                             AND lower(COALESCE(json_extract(payload_json, '$.decision'), '')) = 'pass'
                            THEN 'pass'
                        WHEN lower(COALESCE(json_extract(payload_json, '$.eligibility'), 'eligible')) = 'eligible'
                             AND lower(COALESCE(json_extract(payload_json, '$.decision'), '')) = 'fail'
                            THEN 'fail'
                        ELSE 'skipped'
                    END AS outcome,
                    CASE WHEN json_extract(payload_json, '$.updateSuppressed') = 1 THEN 1 ELSE 0 END AS suppressed,
                    json_extract(payload_json, '$.estimatedCost.amount') AS cost_amount,
                    json_extract(payload_json, '$.estimatedCost.currency') AS cost_currency
                FROM evaluation_audits
                WHERE evaluated_at_utc >= $start AND evaluated_at_utc < $end
            )
            SELECT
                SUM(CASE WHEN outcome IN ('pass', 'fail', 'error') THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 'pass' THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 'fail' THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 'not_eligible' THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 'skipped' THEN 1 ELSE 0 END),
                SUM(suppressed),
                SUM(CASE WHEN outcome IN ('pass', 'fail', 'error') AND cost_amount IS NOT NULL
                              AND cost_currency IS NOT NULL THEN cost_amount ELSE 0 END),
                SUM(CASE WHEN outcome IN ('pass', 'fail', 'error') AND cost_amount IS NOT NULL
                              AND cost_currency IS NOT NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome IN ('pass', 'fail', 'error') AND (cost_amount IS NULL
                              OR cost_currency IS NULL) THEN 1 ELSE 0 END),
                COUNT(DISTINCT CASE WHEN outcome IN ('pass', 'fail', 'error') AND cost_amount IS NOT NULL
                              AND cost_currency IS NOT NULL THEN upper(cost_currency) END),
                MIN(CASE WHEN outcome IN ('pass', 'fail', 'error') AND cost_amount IS NOT NULL
                              AND cost_currency IS NOT NULL THEN upper(cost_currency) END)
            FROM classified;
            """;
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The Home summary aggregate returned no row.");

        var evaluated = Integer(reader, 0);
        var withCost = Integer(reader, 8);
        var withoutCost = Integer(reader, 9);
        var currencyCount = Integer(reader, 10);
        EstimatedCost? cost = null;
        if (withCost > 0 && currencyCount == 1)
        {
            var amount = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            cost = new EstimatedCost(amount, reader.GetString(11));
        }

        return new HomeSummaryAggregate(
            start, end, evaluated, Integer(reader, 1), Integer(reader, 2), Integer(reader, 3),
            Integer(reader, 4), Integer(reader, 5), Integer(reader, 6), cost, withCost, withoutCost,
            withoutCost == 0 && currencyCount <= 1);
    }

    private static int Integer(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

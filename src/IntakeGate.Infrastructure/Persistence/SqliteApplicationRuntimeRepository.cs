using System.Globalization;
using IntakeGate.Application.Persistence;
using IntakeGate.Domain;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteApplicationRuntimeRepository : IApplicationRuntimeRepository
{
    private const string TableName = "application_runtime_records";
    private readonly string _connectionString;

    public SqliteApplicationRuntimeRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task AddAsync(
        ApplicationRuntimeRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (id, instance_id, started_at_utc, application_version)
            VALUES ($id, $instanceId, $startedAtUtc, $applicationVersion);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$instanceId", record.InstanceId.ToString("D"));
        command.Parameters.AddWithValue(
            "$startedAtUtc",
            record.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$applicationVersion", record.ApplicationVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApplicationRuntimeRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var records = new List<ApplicationRuntimeRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, instance_id, started_at_utc, application_version
            FROM {TableName}
            ORDER BY started_at_utc, id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ApplicationRuntimeRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).ToUniversalTime(),
                reader.GetString(3)));
        }

        return records;
    }

    public async Task<bool> CanAccessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {TableName};";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

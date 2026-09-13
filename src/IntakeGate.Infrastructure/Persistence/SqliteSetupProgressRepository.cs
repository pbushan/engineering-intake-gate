using IntakeGate.Application.Audit;
using IntakeGate.Application.Setup;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteSetupProgressRepository : ISetupProgressRepository
{
    private readonly string connectionString;

    public SqliteSetupProgressRepository(string databasePath)
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

    public async Task<SetupProgress> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_visited_step, updated_at_utc
            FROM setup_progress WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SetupProgress(
                Enum.Parse<SetupStep>(reader.GetString(0), ignoreCase: false),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture))
            : new SetupProgress(null, null);
    }

    public async Task RecordAsync(
        SetupStep step,
        DateTimeOffset atUtc,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(step)) throw new ArgumentOutOfRangeException(nameof(step));
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));

        var utc = atUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO setup_progress
                (singleton_id, last_visited_step, updated_at_utc, updated_by_user_id)
            VALUES (1, $step, $at, $actor)
            ON CONFLICT(singleton_id) DO UPDATE SET
                last_visited_step = excluded.last_visited_step,
                updated_at_utc = excluded.updated_at_utc,
                updated_by_user_id = excluded.updated_by_user_id;
            """;
        command.Parameters.AddWithValue("$step", step.ToString());
        command.Parameters.AddWithValue("$at", utc.ToString("O"));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), utc, actor, "SetupProgressRecorded",
                "Setup", "singleton", ["lastVisitedStep"]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}

using IntakeGate.Application.Authentication;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Persistence;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteLocalUserRepository : ILocalUserRepository
{
    private readonly string connectionString;

    public SqliteLocalUserRepository(string databasePath)
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

    public async Task<bool> AnyUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM local_users);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<LocalUser?> FindByNormalizedUsernameAsync(
        string normalizedUsername,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE normalized_username = $normalized_username;";
        command.Parameters.AddWithValue("$normalized_username", normalizedUsername);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<LocalUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE user_id = $user_id;";
        command.Parameters.AddWithValue("$user_id", id.ToString("D"));
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY normalized_username, user_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<LocalUser>();
        while (await reader.ReadAsync(cancellationToken)) users.Add(Read(reader));
        return users;
    }

    public async Task<bool> TryBootstrapAdminAsync(
        Guid id,
        string username,
        string normalizedUsername,
        string? displayName,
        string passwordHash,
        DateTimeOffset nowUtc,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        // One database statement owns the zero-users predicate and insert. SQLite serializes
        // competing writers, so a racing request re-evaluates NOT EXISTS after the winner commits.
        command.CommandText = """
            INSERT INTO local_users
                (user_id, username, normalized_username, display_name, password_hash, role,
                 created_at_utc, password_changed_at_utc)
            SELECT $user_id, $username, $normalized_username, $display_name, $password_hash, 'Admin', $now, $now
            WHERE NOT EXISTS (SELECT 1 FROM local_users);
            """;
        AddUserParameters(command, id, username, normalizedUsername, displayName, passwordHash, nowUtc);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await SqliteSecretStore.InsertAuditAsync(connection, transaction, audit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<bool> TryCreateAsync(
        LocalUser user,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO local_users
                (user_id, username, normalized_username, display_name, password_hash, role,
                 created_at_utc, password_changed_at_utc)
            VALUES ($user_id, $username, $normalized_username, $display_name, $password_hash, $role, $now, $now);
            """;
        AddUserParameters(command, user.Id, user.Username, user.NormalizedUsername, user.DisplayName,
            user.PasswordHash, user.CreatedAtUtc);
        command.Parameters.AddWithValue("$role", user.Role.ToString());
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await SqliteSecretStore.InsertAuditAsync(connection, transaction, audit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<bool> TryReplacePasswordHashAsync(
        Guid id,
        string expectedPasswordHash,
        string replacementPasswordHash,
        DateTimeOffset changedAtUtc,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE local_users
            SET password_hash = $replacement_hash,
                password_changed_at_utc = $changed_at
            WHERE user_id = $user_id AND password_hash = $expected_hash;
            """;
        command.Parameters.AddWithValue("$replacement_hash", replacementPasswordHash);
        command.Parameters.AddWithValue("$changed_at", changedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$user_id", id.ToString("D"));
        command.Parameters.AddWithValue("$expected_hash", expectedPasswordHash);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await SqliteSecretStore.InsertAuditAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private const string SelectColumns = """
        SELECT user_id, username, normalized_username, display_name, password_hash, role,
               created_at_utc, password_changed_at_utc
        FROM local_users
        """;

    private static void AddUserParameters(
        SqliteCommand command,
        Guid id,
        string username,
        string normalizedUsername,
        string? displayName,
        string passwordHash,
        DateTimeOffset nowUtc)
    {
        command.Parameters.AddWithValue("$user_id", id.ToString("D"));
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$normalized_username", normalizedUsername);
        command.Parameters.AddWithValue("$display_name", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$password_hash", passwordHash);
        command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
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

    private static async Task<LocalUser?> ReadSingleAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LocalUser Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        Enum.Parse<LocalUserRole>(reader.GetString(5), ignoreCase: false),
        DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
}

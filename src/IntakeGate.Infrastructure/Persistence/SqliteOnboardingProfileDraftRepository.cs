using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Setup;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteOnboardingProfileDraftRepository(string databasePath)
    : IOnboardingProfileDraftRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task<OnboardingProfileDraft?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    public async Task<OnboardingDraftPersistenceResult> InitializeAsync(
        OnboardingProfileDraftValues values,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(values);
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ProfileExistsAsync(connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.ProfileAlreadyExists);
        }
        var existing = await ReadAsync(connection, transaction, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.Succeeded, existing);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO onboarding_profile_draft
                (singleton_id, draft_json, revision, created_at_utc, updated_at_utc, updated_by_user_id)
            VALUES (1, $draft, 1, $now, $now, $actor);
            """;
        command.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(values, JsonOptions));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "OnboardingProfileDraftInitialized",
            ["serverDefaults", "draftRevision"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OnboardingDraftPersistenceStatus.Succeeded,
            new OnboardingProfileDraft(1, values, now, now));
    }

    public async Task<OnboardingDraftPersistenceResult> UpdateAsync(
        int expectedRevision,
        OnboardingProfileDraftValues values,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(values);
        if (expectedRevision <= 0) return new(OnboardingDraftPersistenceStatus.Conflict);
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ProfileExistsAsync(connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.ProfileAlreadyExists);
        }
        var current = await ReadAsync(connection, transaction, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.NotFound);
        }
        if (current.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.Conflict);
        }
        var nextRevision = checked(expectedRevision + 1);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE onboarding_profile_draft
            SET draft_json = $draft, revision = $next, updated_at_utc = $now,
                updated_by_user_id = $actor
            WHERE singleton_id = 1 AND revision = $expected;
            """;
        command.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(values, JsonOptions));
        command.Parameters.AddWithValue("$next", nextRevision);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        command.Parameters.AddWithValue("$expected", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.Conflict);
        }
        await InsertAuditAsync(connection, transaction, actor, now, "OnboardingProfileDraftUpdated",
            ["profileMetadata", "intakeState", "aiRuntime", "schedule", "processing", "audit", "exclusions", "policy", "draftRevision"],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OnboardingDraftPersistenceStatus.Succeeded,
            new OnboardingProfileDraft(nextRevision, values, current.CreatedAtUtc, now));
    }

    public async Task<OnboardingDraftPersistenceResult> ReplaceForImportAsync(
        int? expectedRevision,
        OnboardingProfileDraftValues values,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(values);
        var now = nowUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, cancellationToken);
        if ((current is null && expectedRevision is not null) ||
            (current is not null && expectedRevision != current.Revision))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnboardingDraftPersistenceStatus.Conflict);
        }
        var nextRevision = checked((current?.Revision ?? 0) + 1);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO onboarding_profile_draft
                (singleton_id, draft_json, revision, created_at_utc, updated_at_utc, updated_by_user_id)
            VALUES (1, $draft, $revision, $created, $now, $actor)
            ON CONFLICT(singleton_id) DO UPDATE SET
                draft_json = excluded.draft_json,
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                updated_by_user_id = excluded.updated_by_user_id;
            """;
        command.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(values, JsonOptions));
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.Parameters.AddWithValue("$created", Format(current?.CreatedAtUtc ?? now));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$actor", actor.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(connection, transaction, actor, now, "ProfilePortableImportStaged",
            ["profile", "policy", "draftRevision"], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OnboardingDraftPersistenceStatus.Succeeded,
            new OnboardingProfileDraft(nextRevision, values, current?.CreatedAtUtc ?? now, now));
    }

    private static async Task<OnboardingProfileDraft?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT draft_json, revision, created_at_utc, updated_at_utc
            FROM onboarding_profile_draft WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var values = JsonSerializer.Deserialize<OnboardingProfileDraftValues>(reader.GetString(0), JsonOptions)
            ?? throw new InvalidOperationException("The persisted onboarding profile draft is empty.");
        return new OnboardingProfileDraft(reader.GetInt32(1), values,
            Parse(reader.GetString(2)), Parse(reader.GetString(3)));
    }

    private static async Task<bool> ProfileExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM singleton_profile_configuration WHERE singleton_id = 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static Task InsertAuditAsync(SqliteConnection connection, SqliteTransaction transaction,
        AuditActor actor, DateTimeOffset now, string operation, IReadOnlyList<string> fields,
        CancellationToken cancellationToken) =>
        SqliteSecretStore.InsertAuditAsync(connection, transaction,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor, operation,
                "OnboardingProfileDraft", "singleton", fields), cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static void ValidateActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}

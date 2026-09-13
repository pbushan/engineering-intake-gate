using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Secrets;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Secrets;

public sealed partial class SqliteSecretStore : ISecretStore
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly string connectionString;
    private readonly InstallationKeyProvider keys;
    private readonly Func<string, string?> environment;
    private readonly Func<DateTimeOffset> utcNow;

    public SqliteSecretStore(
        string databasePath,
        InstallationKeyProvider keys,
        Func<string, string?>? environment = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.keys = keys;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var encrypted = await HasEncryptedCredentialsAsync(cancellationToken);
        var existing = await keys.ValidateExistingAsync(cancellationToken);
        if (existing is not null) CryptographicOperations.ZeroMemory(existing);
        if (encrypted && existing is null) throw new InstallationKeyException("EncryptionKeyMissing");
    }

    public async Task<CredentialMetadata> GetMetadataAsync(
        CredentialSlot slot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_kind, created_at_utc, updated_at_utc, verification_status,
                   last_verified_at_utc, verification_diagnostic
            FROM credential_slots WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue("$slot", slot.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMetadata(slot, reader)
            : EmptyMetadata(slot);
    }

    public async Task<IReadOnlyList<CredentialMetadata>> ListMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<CredentialMetadata>();
        foreach (var slot in Enum.GetValues<CredentialSlot>())
            result.Add(await GetMetadataAsync(slot, cancellationToken));
        return result;
    }

    public async Task<SecretResolution> ResolveAsync(
        CredentialSlot slot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_kind, ciphertext, nonce, authentication_tag, environment_variable_name
            FROM credential_slots WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue("$slot", slot.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return SecretResolution.Unavailable(SecretAvailability.NotConfigured);

        var source = Enum.Parse<SecretSourceKind>(reader.GetString(0), ignoreCase: false);
        if (source == SecretSourceKind.EnvironmentReference)
        {
            var value = environment(reader.GetString(4));
            return string.IsNullOrEmpty(value)
                ? SecretResolution.Unavailable(SecretAvailability.EnvironmentVariableUnavailable)
                : SecretResolution.Available(new SecretValue(value));
        }

        var ciphertext = (byte[])reader[1];
        var nonce = (byte[])reader[2];
        var tag = (byte[])reader[3];
        var key = await keys.GetOrCreateAsync(encryptedCredentialsExist: true, cancellationToken);
        try
        {
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(slot));
                return SecretResolution.Available(new SecretValue(Encoding.UTF8.GetString(plaintext)));
            }
            catch (CryptographicException)
            {
                return SecretResolution.Unavailable(SecretAvailability.DecryptionFailed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task ReplaceLocalAsync(
        CredentialSlot slot,
        SecretValue replacement,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateActor(actor);
        var plaintext = Encoding.UTF8.GetBytes(replacement.DangerousGetValue());
        byte[]? key = null;
        try
        {
            key = await keys.GetOrCreateAsync(await HasEncryptedCredentialsAsync(cancellationToken), cancellationToken);
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(slot));

            await PersistReplacementAsync(slot, SecretSourceKind.LocallyEncrypted,
                ciphertext, nonce, tag, null, actor, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task ConfigureEnvironmentReferenceAsync(
        CredentialSlot slot,
        string environmentVariableName,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        var trimmed = environmentVariableName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !EnvironmentVariableName().IsMatch(trimmed))
            throw new ArgumentException("Environment variable reference has an invalid format.", nameof(environmentVariableName));
        return PersistReplacementAsync(slot, SecretSourceKind.EnvironmentReference,
            null, null, null, trimmed, actor, cancellationToken);
    }

    /// <summary>
    /// One-way compatibility import for the legacy profile reference. It never resolves the
    /// environment value and never overwrites a control-plane-owned slot.
    /// </summary>
    public async Task ImportEnvironmentReferenceIfMissingAsync(
        CredentialSlot slot,
        string environmentVariableName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = environmentVariableName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !EnvironmentVariableName().IsMatch(trimmed))
            throw new ArgumentException("Environment variable reference has an invalid format.", nameof(environmentVariableName));
        var now = utcNow().ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status)
            VALUES ($slot, 'EnvironmentReference', $environment, $now, $now, 'NeverVerified');
            """;
        command.Parameters.AddWithValue("$slot", slot.ToString());
        command.Parameters.AddWithValue("$environment", trimmed);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await InsertAuditAsync(connection, transaction,
                new ControlPlaneAuditRecord(Guid.NewGuid(), now, AuditActor.System,
                    "LegacyEnvironmentReferenceImported", "CredentialSlot", slot.ToString(),
                    ["sourceKind", "environmentReference"]), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetVerificationAsync(
        CredentialSlot slot,
        CredentialVerificationStatus status,
        DateTimeOffset? verifiedAtUtc,
        CredentialVerificationDiagnostic? safeDiagnostic,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (safeDiagnostic is not null && !Enum.IsDefined(safeDiagnostic.Value))
            throw new ArgumentOutOfRangeException(nameof(safeDiagnostic));
        if ((status == CredentialVerificationStatus.Verified) != (verifiedAtUtc is not null))
            throw new ArgumentException(
                "Only a verified credential may set the last-successful-verification timestamp.", nameof(verifiedAtUtc));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = utcNow().ToUniversalTime();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE credential_slots
            SET verification_status = $status,
                last_verified_at_utc = CASE
                    WHEN $status = 'Verified' THEN $verified_at
                    WHEN $status = 'NeverVerified' THEN NULL
                    ELSE last_verified_at_utc
                END,
                verification_diagnostic = $diagnostic
            WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$verified_at", verifiedAtUtc is null ? DBNull.Value : verifiedAtUtc.Value.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$diagnostic", safeDiagnostic is null ? DBNull.Value : safeDiagnostic.Value.ToString());
        command.Parameters.AddWithValue("$slot", slot.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new SecretStoreOperationException("CredentialNotConfigured");
        await InsertAuditAsync(connection, transaction, NewAudit(actor, now, "CredentialVerificationUpdated", slot,
            ["verificationStatus", "lastVerifiedAtUtc", "verificationDiagnostic"]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TrySetVerificationIfCurrentAsync(
        CredentialSlot slot,
        DateTimeOffset expectedUpdatedAtUtc,
        CredentialVerificationStatus status,
        DateTimeOffset? verifiedAtUtc,
        CredentialVerificationDiagnostic? safeDiagnostic,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (safeDiagnostic is not null && !Enum.IsDefined(safeDiagnostic.Value))
            throw new ArgumentOutOfRangeException(nameof(safeDiagnostic));
        if ((status == CredentialVerificationStatus.Verified) != (verifiedAtUtc is not null))
            throw new ArgumentException(
                "Only a verified credential may set the last-successful-verification timestamp.", nameof(verifiedAtUtc));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = utcNow().ToUniversalTime();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE credential_slots
            SET verification_status = $status,
                last_verified_at_utc = CASE
                    WHEN $status = 'Verified' THEN $verified_at
                    WHEN $status = 'NeverVerified' THEN NULL
                    ELSE last_verified_at_utc
                END,
                verification_diagnostic = $diagnostic
            WHERE slot = $slot AND updated_at_utc = $expected_updated_at;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$verified_at", verifiedAtUtc is null
            ? DBNull.Value
            : verifiedAtUtc.Value.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$diagnostic", safeDiagnostic is null
            ? DBNull.Value
            : safeDiagnostic.Value.ToString());
        command.Parameters.AddWithValue("$slot", slot.ToString());
        command.Parameters.AddWithValue("$expected_updated_at", expectedUpdatedAtUtc.ToUniversalTime().ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await InsertAuditAsync(connection, transaction, NewAudit(actor, now, "CredentialVerificationUpdated", slot,
            ["verificationStatus", "lastVerifiedAtUtc", "verificationDiagnostic"]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task PersistReplacementAsync(
        CredentialSlot slot,
        SecretSourceKind source,
        byte[]? ciphertext,
        byte[]? nonce,
        byte[]? tag,
        string? environmentVariableName,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var prior = await ReadSourceAsync(connection, transaction, slot, cancellationToken);
        var now = utcNow().ToUniversalTime();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO credential_slots
                (slot, source_kind, ciphertext, nonce, authentication_tag, environment_variable_name,
                 created_at_utc, updated_at_utc, verification_status, last_verified_at_utc,
                 verification_diagnostic)
            VALUES ($slot, $source, $ciphertext, $nonce, $tag, $environment, $now, $now,
                    'NeverVerified', NULL, NULL)
            ON CONFLICT(slot) DO UPDATE SET
                source_kind = excluded.source_kind,
                ciphertext = excluded.ciphertext,
                nonce = excluded.nonce,
                authentication_tag = excluded.authentication_tag,
                environment_variable_name = excluded.environment_variable_name,
                updated_at_utc = excluded.updated_at_utc,
                verification_status = 'NeverVerified',
                last_verified_at_utc = NULL,
                verification_diagnostic = NULL;
            """;
        command.Parameters.AddWithValue("$slot", slot.ToString());
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$ciphertext", (object?)ciphertext ?? DBNull.Value);
        command.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        command.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        command.Parameters.AddWithValue("$environment", (object?)environmentVariableName ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        var operation = prior is null ? "CredentialConfigured" : "CredentialReplaced";
        var fields = prior is not null && prior != source
            ? new[] { "sourceKind", "credential", "verificationStatus" }
            : new[] { "credential", "verificationStatus" };
        await InsertAuditAsync(connection, transaction, NewAudit(actor, now, operation, slot, fields), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> HasEncryptedCredentialsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM credential_slots WHERE source_kind = 'LocallyEncrypted');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<SecretSourceKind?> ReadSourceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CredentialSlot slot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT source_kind FROM credential_slots WHERE slot = $slot;";
        command.Parameters.AddWithValue("$slot", slot.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? Enum.Parse<SecretSourceKind>(text, ignoreCase: false) : null;
    }

    internal static async Task InsertAuditAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ControlPlaneAuditRecord audit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO control_plane_audits
                (audit_id, occurred_at_utc, actor_user_id, actor_username, operation,
                 target_category, target_id, changed_fields_json)
            VALUES ($id, $at, $actor_id, $actor_username, $operation, $category, $target, $fields);
            """;
        command.Parameters.AddWithValue("$id", audit.Id.ToString("D"));
        command.Parameters.AddWithValue("$at", audit.OccurredAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$actor_id", audit.Actor.Id.ToString("D"));
        command.Parameters.AddWithValue("$actor_username", audit.Actor.Username);
        command.Parameters.AddWithValue("$operation", audit.Operation);
        command.Parameters.AddWithValue("$category", audit.TargetCategory);
        command.Parameters.AddWithValue("$target", audit.TargetId);
        command.Parameters.AddWithValue("$fields", System.Text.Json.JsonSerializer.Serialize(audit.ChangedFields));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static ControlPlaneAuditRecord NewAudit(
        AuditActor actor, DateTimeOffset at, string operation, CredentialSlot slot, IReadOnlyList<string> fields) =>
        new(Guid.NewGuid(), at, actor, operation, "CredentialSlot", slot.ToString(), fields);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static CredentialMetadata ReadMetadata(CredentialSlot slot, SqliteDataReader reader) => new(
        slot,
        true,
        Enum.Parse<SecretSourceKind>(reader.GetString(0), ignoreCase: false),
        DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
        Enum.Parse<CredentialVerificationStatus>(reader.GetString(3), ignoreCase: false),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(5) ? null : Enum.Parse<CredentialVerificationDiagnostic>(reader.GetString(5), ignoreCase: false));

    private static CredentialMetadata EmptyMetadata(CredentialSlot slot) => new(
        slot, false, null, null, null, CredentialVerificationStatus.NeverVerified, null, null);

    private static byte[] AssociatedData(CredentialSlot slot) =>
        Encoding.UTF8.GetBytes($"engineering-intake-gate:credential:v1:{slot}");

    private static void ValidateActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == Guid.Empty || string.IsNullOrWhiteSpace(actor.Username))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnvironmentVariableName();
}

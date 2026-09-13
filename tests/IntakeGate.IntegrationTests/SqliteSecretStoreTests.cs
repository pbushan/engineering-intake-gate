using System.Text;
using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Authentication;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Setup;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.AiManagement;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Persistence;
using IntakeGate.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteSecretStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-secrets-{Guid.NewGuid():N}");
    private static readonly AuditActor Actor = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "security-admin");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");

    [Fact]
    public async Task SEC_003_LocalSecretIsRandomizedEncryptedRetrievableAndDurableAcrossRestart()
    {
        var (database, keyPath, store) = await CreateAsync();
        var canary = Distinctive("local");
        await store.ReplaceLocalAsync(CredentialSlot.OpenAi, new SecretValue(canary), Actor);
        var first = await ReadCipherAsync(database, CredentialSlot.OpenAi);

        await store.ReplaceLocalAsync(CredentialSlot.OpenAi, new SecretValue(canary), Actor);
        var second = await ReadCipherAsync(database, CredentialSlot.OpenAi);

        Assert.NotEqual(Convert.ToHexString(first.Nonce), Convert.ToHexString(second.Nonce));
        Assert.NotEqual(Convert.ToHexString(first.Ciphertext), Convert.ToHexString(second.Ciphertext));
        Assert.DoesNotContain(canary, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)), StringComparison.Ordinal);
        Assert.Equal(32, (await File.ReadAllBytesAsync(keyPath)).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));

        var restarted = new SqliteSecretStore(database, new InstallationKeyProvider(keyPath));
        await restarted.ValidateAsync();
        var resolved = await restarted.ResolveAsync(CredentialSlot.OpenAi);
        Assert.Equal(SecretAvailability.Available, resolved.Availability);
        Assert.Equal(canary, resolved.Secret!.DangerousGetValue());
    }

    [Fact]
    public async Task SEC_004_MissingOrCorruptInstallationKeyFailsSafelyWithoutDeletingCredential()
    {
        var (database, keyPath, store) = await CreateAsync();
        await store.ReplaceLocalAsync(CredentialSlot.Anthropic, new SecretValue(Distinctive("key-loss")), Actor);
        File.Delete(keyPath);

        var missing = new SqliteSecretStore(database, new InstallationKeyProvider(keyPath));
        var missingError = await Assert.ThrowsAsync<InstallationKeyException>(() => missing.ValidateAsync());
        Assert.Equal("EncryptionKeyMissing", missingError.SafeCategory);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM credential_slots;"));

        await File.WriteAllBytesAsync(keyPath, [1, 2, 3]);
        var corrupt = new SqliteSecretStore(database, new InstallationKeyProvider(keyPath));
        var corruptError = await Assert.ThrowsAsync<InstallationKeyException>(() => corrupt.ValidateAsync());
        Assert.Equal("EncryptionKeyMalformed", corruptError.SafeCategory);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM credential_slots;"));
    }

    [Fact]
    public async Task SEC_005_CorruptCiphertextReturnsOnlySafeFailureAndPreservesRow()
    {
        var (database, _, store) = await CreateAsync();
        var canary = Distinctive("cipher");
        await store.ReplaceLocalAsync(CredentialSlot.AzureDevOps, new SecretValue(canary), Actor);
        await ExecuteAsync(database, "UPDATE credential_slots SET ciphertext = zeroblob(length(ciphertext)) WHERE slot = 'AzureDevOps';");

        var result = await store.ResolveAsync(CredentialSlot.AzureDevOps);

        Assert.Equal(SecretAvailability.DecryptionFailed, result.Availability);
        Assert.Null(result.Secret);
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM credential_slots;"));
    }

    [Fact]
    public async Task SEC_006_EnvironmentReferencePersistsOnlyNameAndResolvesCurrentProcessValue()
    {
        var (database, keyPath, _) = await CreateAsync();
        var variable = $"INTAKE_GATE_TEST_{Guid.NewGuid():N}".ToUpperInvariant();
        var canary = Distinctive("environment");
        var values = new Dictionary<string, string?> { [variable] = canary };
        var store = new SqliteSecretStore(database, new InstallationKeyProvider(keyPath),
            name => values.GetValueOrDefault(name), () => Now);

        await store.ConfigureEnvironmentReferenceAsync(CredentialSlot.AzureDevOps, variable, Actor);
        var resolved = await store.ResolveAsync(CredentialSlot.AzureDevOps);
        Assert.Equal(canary, resolved.Secret!.DangerousGetValue());
        Assert.Equal(variable, await ScalarAsync<string>(database,
            "SELECT environment_variable_name FROM credential_slots WHERE slot = 'AzureDevOps';"));
        Assert.DoesNotContain(canary, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)), StringComparison.Ordinal);
        Assert.False(File.Exists(keyPath));

        values[variable] = null;
        var unavailable = await store.ResolveAsync(CredentialSlot.AzureDevOps);
        Assert.Equal(SecretAvailability.EnvironmentVariableUnavailable, unavailable.Availability);
        Assert.Null(unavailable.Secret);
    }

    [Fact]
    public async Task SEC_007_ReplacementIsAtomicAndResetsVerificationWithoutExposingValuesInMetadataOrAudit()
    {
        var (database, _, store) = await CreateAsync();
        var original = Distinctive("original");
        var replacement = Distinctive("replacement");
        await store.ReplaceLocalAsync(CredentialSlot.OpenAi, new SecretValue(original), Actor);
        await store.SetVerificationAsync(CredentialSlot.OpenAi, CredentialVerificationStatus.Verified,
            Now, null, Actor);
        await store.ReplaceLocalAsync(CredentialSlot.OpenAi, new SecretValue(replacement), Actor);

        var metadata = await store.GetMetadataAsync(CredentialSlot.OpenAi);
        Assert.True(metadata.Configured);
        Assert.Equal(SecretSourceKind.LocallyEncrypted, metadata.SourceKind);
        Assert.Equal(CredentialVerificationStatus.NeverVerified, metadata.VerificationStatus);
        Assert.Null(metadata.LastVerifiedAtUtc);
        Assert.Null(metadata.VerificationDiagnostic);
        var metadataJson = JsonSerializer.Serialize(metadata);
        Assert.DoesNotContain(original, metadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement, metadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", metadataJson, StringComparison.OrdinalIgnoreCase);

        var audit = await new SqliteAuditRepository(database).ListControlPlaneAsync();
        var auditJson = JsonSerializer.Serialize(audit);
        Assert.All(audit, item => Assert.Equal(Actor.Id, item.Actor.Id));
        Assert.DoesNotContain(original, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", auditJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replacement, (await store.ResolveAsync(CredentialSlot.OpenAi)).Secret!.DangerousGetValue());
    }

    [Fact]
    public async Task SEC_008_FailedAuditedReplacementRollsBackAndPreservesPriorCredential()
    {
        var (database, _, store) = await CreateAsync();
        var original = Distinctive("atomic-original");
        var rejected = Distinctive("atomic-rejected");
        await store.ReplaceLocalAsync(CredentialSlot.Anthropic, new SecretValue(original), Actor);
        await ExecuteAsync(database, "DROP TABLE control_plane_audits;");

        await Assert.ThrowsAsync<SqliteException>(() => store.ReplaceLocalAsync(
            CredentialSlot.Anthropic, new SecretValue(rejected), Actor));

        var resolved = await store.ResolveAsync(CredentialSlot.Anthropic);
        Assert.Equal(original, resolved.Secret!.DangerousGetValue());
        Assert.DoesNotContain(rejected, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SEC_011_SourceChangeLeavesOneUnambiguousActiveRepresentation()
    {
        var (database, _, store) = await CreateAsync();
        await store.ReplaceLocalAsync(CredentialSlot.AzureDevOps,
            new SecretValue(Distinctive("superseded-local")), Actor);
        await store.ConfigureEnvironmentReferenceAsync(
            CredentialSlot.AzureDevOps, "ACTIVE_ADO_REFERENCE", Actor);

        Assert.Equal("EnvironmentReference", await ScalarAsync<string>(database,
            "SELECT source_kind FROM credential_slots WHERE slot = 'AzureDevOps';"));
        Assert.Equal(0L, await ScalarAsync<long>(database,
            "SELECT COUNT(*) FROM credential_slots WHERE slot = 'AzureDevOps' AND (ciphertext IS NOT NULL OR nonce IS NOT NULL OR authentication_tag IS NOT NULL);"));
        var audit = Assert.Single(await new SqliteAuditRepository(database).ListControlPlaneAsync(),
            item => item.ChangedFields.Contains("sourceKind", StringComparer.Ordinal));
        Assert.Equal("CredentialReplaced", audit.Operation);
        Assert.Contains("sourceKind", audit.ChangedFields);
    }

    [Fact]
    public async Task CFG_011_SetupProgressPersistsAsNavigationOnlyWithActorAwareAudit()
    {
        var (database, _, _) = await CreateAsync();
        var first = new SqliteSetupProgressRepository(database);
        await first.RecordAsync(SetupStep.AzureDevOps, Now, Actor);

        var restarted = new SqliteSetupProgressRepository(database);
        var progress = await restarted.GetAsync();
        Assert.Equal(SetupStep.AzureDevOps, progress.LastVisitedStep);
        Assert.Equal(Now, progress.UpdatedAtUtc);
        var audit = Assert.Single(await new SqliteAuditRepository(database).ListControlPlaneAsync());
        Assert.Equal("SetupProgressRecorded", audit.Operation);
        Assert.Equal(Actor, audit.Actor);
        Assert.Equal(["lastVisitedStep"], audit.ChangedFields);
    }

    [Fact]
    public async Task CFG_012_SetupReadinessIsDerivedFromUsersProfileAndRequiredCredentials()
    {
        var (database, _, store) = await CreateAsync();
        var profilePath = Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml");
        var configuration = new YamlDeploymentConfigurationLoader().Load(profilePath);
        var users = new SqliteLocalUserRepository(database);
        var profiles = new SqliteSingletonProfileRepository(database);
        var progress = new SqliteSetupProgressRepository(database);
        var adoConfigurations = new SqliteAzureDevOpsConfigurationRepository(
            database, new DeploymentConfigurationValidator());
        var aiConfigurations = new SqliteAiConfigurationRepository(
            database, new DeploymentConfigurationValidator());
        var runtimeState = new DeploymentConfigurationState(configuration);
        var service = new SetupStateService(
            new SqliteApplicationRuntimeRepository(database), users, profiles, store, progress,
            runtimeState, adoConfigurations, aiConfigurations);

        var empty = await service.GetAsync();
        Assert.False(empty.SetupComplete);
        Assert.False(empty.AdminConfigured);
        Assert.False(empty.ProfileConfigured);

        await users.TryBootstrapAdminAsync(Actor.Id, Actor.Username, Actor.Username.ToUpperInvariant(), null,
            "synthetic-hash", Now,
            new ControlPlaneAuditRecord(Guid.NewGuid(), Now, Actor, "BootstrapAdmin", "LocalUser",
                Actor.Id.ToString("D"), ["username", "role"]));
        await profiles.CreateAsync(configuration);
        await store.ConfigureEnvironmentReferenceAsync(
            CredentialSlot.AzureDevOps, "SETUP_ADO_REFERENCE", Actor);
        var partial = await service.GetAsync();
        Assert.False(partial.SetupComplete);
        Assert.True(partial.AdminConfigured);
        Assert.True(partial.ProfileConfigured);
        Assert.True(partial.AzureDevOpsCredentialConfigured);
        Assert.False(partial.AiCredentialConfigured);

        await store.ConfigureEnvironmentReferenceAsync(CredentialSlot.OpenAi, "SETUP_AI_REFERENCE", Actor);
        Assert.False((await service.GetAsync()).SetupComplete);
        await store.SetVerificationAsync(CredentialSlot.OpenAi, CredentialVerificationStatus.Verified,
            Now, null, Actor);
        Assert.False((await service.GetAsync()).SetupComplete);
        var aiState = await aiConfigurations.GetAsync();
        var aiCredential = await store.GetMetadataAsync(CredentialSlot.OpenAi);
        await aiConfigurations.CommitValidatedAsync(configuration, "openai", configuration.Profile.Ai.Model,
            aiState.Fingerprint, aiCredential.UpdatedAtUtc!.Value, Actor, Now);
        runtimeState.RequireAiRuntimeActivation();
        Assert.False((await service.GetAsync()).SetupComplete);
        await store.SetVerificationAsync(CredentialSlot.AzureDevOps, CredentialVerificationStatus.Verified,
            Now, null, Actor);
        Assert.False((await service.GetAsync()).SetupComplete);
        var adoFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            configuration.Profile.Identity.Id, configuration.Profile.Ado);
        await adoConfigurations.CommitValidatedAsync(configuration, adoFingerprint, adoFingerprint,
            Actor, Now);
        var aiReadyButActivationDeferred = await service.GetAsync();
        Assert.False(aiReadyButActivationDeferred.SetupComplete);
        Assert.True(aiReadyButActivationDeferred.AzureDevOpsCredentialVerified);
        Assert.True(aiReadyButActivationDeferred.AzureDevOpsSavedQueryConfirmed);
        Assert.True(aiReadyButActivationDeferred.AiCredentialVerified);
        Assert.True(aiReadyButActivationDeferred.AiModelConfigured);
        Assert.False(aiReadyButActivationDeferred.RuntimeActivationCurrent);
        Assert.Equal(CredentialSlot.OpenAi, aiReadyButActivationDeferred.RequiredAiCredentialSlot);
    }

    private async Task<(string Database, string KeyPath, SqliteSecretStore Store)> CreateAsync()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        var key = Path.Combine(directory, $"{Guid.NewGuid():N}.key");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        return (database, key, new SqliteSecretStore(database, new InstallationKeyProvider(key), utcNow: () => Now));
    }

    private static string Distinctive(string category) =>
        string.Concat("phase", "2b-", category, "-", Guid.NewGuid().ToString("N"));

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EngineeringIntakeGate.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static async Task<(byte[] Ciphertext, byte[] Nonce)> ReadCipherAsync(string database, CredentialSlot slot)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ciphertext, nonce FROM credential_slots WHERE slot = $slot;";
        command.Parameters.AddWithValue("$slot", slot.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return ((byte[])reader[0], (byte[])reader[1]);
    }

    private static async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

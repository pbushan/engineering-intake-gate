using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace IntakeGate.AcceptanceTests;

internal static class LegacyProfileTestSeeder
{
    public static async Task ImportAsync(string databasePath, string exactProfilePath)
    {
        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        await new LegacyProfileImporter(new SqliteSingletonProfileRepository(databasePath))
            .ImportAsync(exactProfilePath);
        var configuration = (await new SqliteSingletonProfileRepository(databasePath).LoadAsync())!;
        Environment.SetEnvironmentVariable(configuration.Profile.Ado.Authentication.EnvironmentVariable,
            "acceptance-ado-runtime-credential");
        Environment.SetEnvironmentVariable(configuration.Profile.Ai.Authentication.EnvironmentVariable,
            "acceptance-ai-runtime-credential");
        var now = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O");
        var aiSlot = configuration.Profile.Ai.Provider == AiProviderNames.OpenAi ? "OpenAi" : "Anthropic";
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status, last_verified_at_utc)
            VALUES ('AzureDevOps', 'EnvironmentReference', $adoEnvironment, $now, $now, 'Verified', $now);
            INSERT OR REPLACE INTO credential_slots
                (slot, source_kind, environment_variable_name, created_at_utc, updated_at_utc,
                 verification_status, last_verified_at_utc)
            VALUES ($aiSlot, 'EnvironmentReference', $aiEnvironment, $now, $now, 'Verified', $now);
            INSERT OR REPLACE INTO ado_configuration_state
                (singleton_id, profile_id, configuration_generation, configuration_fingerprint,
                 saved_query_id, query_validated_at_utc, updated_at_utc)
            VALUES (1, $profileId, 1, $adoFingerprint, $queryId, $now, $now);
            INSERT OR REPLACE INTO ai_configuration_state
                (singleton_id, profile_id, provider, model_id, credential_updated_at_utc,
                 model_validated_at_utc, updated_at_utc)
            VALUES (1, $profileId, $provider, $model, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$adoEnvironment", configuration.Profile.Ado.Authentication.EnvironmentVariable);
        command.Parameters.AddWithValue("$aiSlot", aiSlot);
        command.Parameters.AddWithValue("$aiEnvironment", configuration.Profile.Ai.Authentication.EnvironmentVariable);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$profileId", configuration.Profile.Identity.Id);
        command.Parameters.AddWithValue("$adoFingerprint", AzureDevOpsConfigurationFingerprint.Create(
            configuration.Profile.Identity.Id, configuration.Profile.Ado));
        command.Parameters.AddWithValue("$queryId", configuration.Profile.Ado.SavedQueryId.ToString("D"));
        command.Parameters.AddWithValue("$provider", configuration.Profile.Ai.Provider);
        command.Parameters.AddWithValue("$model", configuration.Profile.Ai.Model);
        await command.ExecuteNonQueryAsync();
        var revision = DateTimeOffset.Parse(now);
        await new SqliteRuntimeConfigurationGenerationRepository(databasePath, new DeploymentConfigurationValidator())
            .ActivateAsync(configuration,
                RuntimeConfigurationFingerprint.Create(configuration, revision, revision),
                new RuntimeCredentialBinding(CredentialSlot.AzureDevOps,
                    SecretSourceKind.EnvironmentReference, revision),
                new RuntimeCredentialBinding(configuration.Profile.Ai.Provider == AiProviderNames.OpenAi
                        ? CredentialSlot.OpenAi : CredentialSlot.Anthropic,
                    SecretSourceKind.EnvironmentReference, revision),
                new AuditActor(Guid.Parse("00000000-0000-0000-0000-000000000002"), "deterministic-test"),
                revision, ["deterministicTestSeed"]);
    }
}

using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class Phase2BSecretHostTests
{
    [Fact]
    public async Task SEC_003_SEC_004_ProfilelessHostRestartRetainsSecretAndMissingKeyFailsStartupSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-host-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "intake-gate.db");
        var keyPath = Path.Combine(directory, "installation.key");
        var overridePath = Path.Combine(directory, "appsettings.json");
        var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        var canary = string.Concat("host-restart-", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = database },
                SecretStore = new { KeyPath = keyPath }
            }));
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);

            await using (var first = Factory())
            {
                using var client = first.CreateClient();
                var store = first.Services.GetRequiredService<ISecretStore>();
                await store.ReplaceLocalAsync(CredentialSlot.OpenAi, new SecretValue(canary),
                    new AuditActor(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "restart-admin"));
            }

            await using (var restarted = Factory())
            {
                using var client = restarted.CreateClient();
                var resolved = await restarted.Services.GetRequiredService<ISecretStore>()
                    .ResolveAsync(CredentialSlot.OpenAi);
                Assert.Equal(SecretAvailability.Available, resolved.Availability);
                Assert.Equal(canary, resolved.Secret!.DangerousGetValue());
            }

            File.Delete(keyPath);
            await using var missingKey = Factory();
            Assert.ThrowsAny<Exception>(() => missingKey.CreateClient());
            Assert.Equal(1L, await ScalarAsync(database,
                "SELECT COUNT(*) FROM credential_slots WHERE slot = 'OpenAi' AND ciphertext IS NOT NULL;"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previous);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Test"));

    private static async Task<long> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

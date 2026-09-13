using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class ProfileEndpointTests
{
    [Fact]
    public async Task CFG_007_NormalStartupUsesSQLiteAfterTheLegacySourceIsRemoved()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-source-removal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, "profile.yaml");
        File.Copy(Path.Combine(root, "profiles", "example", "profile.yaml"), profilePath);
        File.Copy(Path.Combine(root, "profiles", "example", "intake-policy.yaml"),
            Path.Combine(directory, "intake-policy.yaml"));
        var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-source-removal-{Guid.NewGuid():N}.db");
        var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-source-removal-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        try
        {
            await LegacyProfileTestSeeder.ImportAsync(databasePath, profilePath);
            Directory.Delete(directory, recursive: true);
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            await AuthenticatedTestClient.AuthenticateAdminAsync(client);

            var profile = await client.GetFromJsonAsync<JsonElement>("/api/profile");

            Assert.Equal("example", profile.GetProperty("profileId").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task CFG_001_AUD_005_ProfileEndpointReturnsOnlySafeMetadata()
    {
        var root = FindRepositoryRoot();
        var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-api-{Guid.NewGuid():N}.db");
        var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-api-{Guid.NewGuid():N}.json");
        var previousOverride = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        try
        {
            var profilePath = Path.Combine(root, "profiles", "example", "profile.yaml");
            await LegacyProfileTestSeeder.ImportAsync(databasePath, profilePath);
            var deploymentOverride = JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            });
            await File.WriteAllTextAsync(overridePath, deploymentOverride);
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            await AuthenticatedTestClient.AuthenticateAdminAsync(client);

            using var response = await client.GetAsync("/api/profile");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.True(json.GetProperty("exists").GetBoolean());
            Assert.Equal("example", json.GetProperty("profileId").GetString());
            Assert.Equal("example-engineering-intake", json.GetProperty("policy").GetProperty("id").GetString());
            Assert.StartsWith("sha256:", json.GetProperty("policy").GetProperty("fingerprint").GetString(), StringComparison.Ordinal);
            var serialized = json.GetRawText();
            Assert.DoesNotContain("authentication", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("environmentVariable", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("patEnvironmentVariable", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKeyEnvironmentVariable", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task CFG_001_RuntimeOverridesStillTakePrecedenceAfterSharedValidation()
    {
        var root = FindRepositoryRoot();
        var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-overrides-{Guid.NewGuid():N}.db");
        var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-overrides-{Guid.NewGuid():N}.json");
        var previousOverride = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        try
        {
            await LegacyProfileTestSeeder.ImportAsync(databasePath,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            var deploymentOverride = JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath },
                AiRuntime = new
                {
                    Provider = "anthropic",
                    Model = "override-model",
                    CredentialEnvironmentVariable = "OVERRIDE_AI_KEY",
                    TimeoutSeconds = 17
                },
                AdoRuntime = new
                {
                    OrganizationUrl = "https://override.invalid",
                    Project = "Override Project",
                    SavedQueryId = "22222222-2222-2222-2222-222222222222",
                    CredentialEnvironmentVariable = "OVERRIDE_ADO_PAT"
                }
            });
            await File.WriteAllTextAsync(overridePath, deploymentOverride);
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var configuration = factory.Services.GetRequiredService<DeploymentConfiguration>();

            Assert.Equal("example", configuration.Profile.Identity.Id);
            Assert.Equal("anthropic", configuration.Profile.Ai.Provider);
            Assert.Equal("override-model", configuration.Profile.Ai.Model);
            Assert.Equal("OVERRIDE_AI_KEY", configuration.Profile.Ai.Authentication.EnvironmentVariable);
            Assert.Equal(17, configuration.Profile.Ai.TimeoutSeconds);
            Assert.Equal(new Uri("https://override.invalid"), configuration.Profile.Ado.OrganizationUrl);
            Assert.Equal("Override Project", configuration.Profile.Ado.Project);
            Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), configuration.Profile.Ado.SavedQueryId);
            Assert.Equal("OVERRIDE_ADO_PAT", configuration.Profile.Ado.Authentication.EnvironmentVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EngineeringIntakeGate.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

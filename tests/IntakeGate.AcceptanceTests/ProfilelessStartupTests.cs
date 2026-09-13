using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.WorkItems;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class ProfilelessStartupTests
{
    [Fact]
    public async Task CFG_008_DB_001_FreshHostIsHealthySetupIncompleteAndNeverExecutesProviders()
    {
        var root = RepositoryRoot();
        var source = new RecordingWorkItemSource();
        var ai = new RecordingAiProvider();
        var adoManagement = new RecordingManagementClientFactory();
        await using var fixture = await Fixture.CreateAsync(
            new
            {
                OperationalDatabase = new { Path = "" },
                Profile = new
                {
                    ConfigurationPath = Path.Combine(root, "profiles", "example", "profile.yaml")
                }
            },
            (services, databasePath) =>
            {
                services.RemoveAll<IWorkItemSource>();
                services.RemoveAll<IIntakeAiProvider>();
                services.RemoveAll<IAzureDevOpsManagementClientFactory>();
                services.AddSingleton<IWorkItemSource>(source);
                services.AddSingleton<IIntakeAiProvider>(ai);
                services.AddSingleton<IAzureDevOpsManagementClientFactory>(adoManagement);
            });
        await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Client);

        using var readiness = await fixture.Client.GetAsync("/health/ready");
        readiness.EnsureSuccessStatusCode();
        var health = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ready", health.GetProperty("status").GetString());
        Assert.Equal("incomplete", health.GetProperty("setupStatus").GetString());

        using (var detailedResponse = await fixture.Client.GetAsync("/api/support/health"))
        {
            detailedResponse.EnsureSuccessStatusCode();
            var detailed = await detailedResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("healthy", detailed.GetProperty("application").GetProperty("status").GetString());
            Assert.Equal("healthy", detailed.GetProperty("database").GetProperty("status").GetString());
            Assert.Equal("incomplete", detailed.GetProperty("setup").GetProperty("status").GetString());
            Assert.Equal("noActiveGeneration", detailed.GetProperty("runtime").GetProperty("status").GetString());
            Assert.Equal("waiting", detailed.GetProperty("scheduler").GetProperty("status").GetString());
            Assert.Equal(0, adoManagement.ConnectionCalls);
            Assert.Equal(0, source.Calls);
            Assert.Equal(0, ai.Calls);
        }
        using (var anonymous = fixture.Factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/support/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit")).StatusCode);
        }

        using (var staged = await fixture.Client.PutAsJsonAsync("/api/ado/settings", new
        {
            organizationUrl = "https://dev.azure.com/profileless-organization/",
            project = "ProfilelessProject"
        }))
            Assert.Equal(HttpStatusCode.NoContent, staged.StatusCode);
        var adoSettings = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/ado/settings");
        Assert.False(adoSettings.GetProperty("profileConfigured").GetBoolean());
        Assert.Equal("https://dev.azure.com/profileless-organization",
            adoSettings.GetProperty("organizationUrl").GetString());
        Assert.Equal("ProfilelessProject", adoSettings.GetProperty("project").GetString());
        using (var credential = await fixture.Client.PutAsJsonAsync("/api/ado/credential/local", new
        {
            replacement = "profileless-synthetic-credential"
        }))
            credential.EnsureSuccessStatusCode();
        using (var connection = await fixture.Client.PostAsync("/api/ado/connection-tests", null))
            connection.EnsureSuccessStatusCode();
        Assert.Equal(1, adoManagement.ConnectionCalls);
        using var validation = await fixture.Client.PostAsJsonAsync("/api/ado/query-candidates/validate", new
        {
            savedQuery = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        });
        validation.EnsureSuccessStatusCode();
        var validationBody = await validation.Content.ReadFromJsonAsync<JsonElement>();
        using (var confirmation = await fixture.Client.PostAsJsonAsync("/api/ado/query-candidates/confirm", new
        {
            confirmationToken = validationBody.GetProperty("confirmationToken").GetString()
        }))
            confirmation.EnsureSuccessStatusCode();
        var confirmedSettings = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/ado/settings");
        Assert.True(confirmedSettings.GetProperty("queryConfirmed").GetBoolean());
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            confirmedSettings.GetProperty("savedQueryId").GetString());
        Assert.Equal(1, adoManagement.QueryCalls);

        using (var profile = await fixture.Client.GetAsync("/api/profile"))
        {
            profile.EnsureSuccessStatusCode();
            var body = await profile.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("exists").GetBoolean());
        }
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Post, "/api/runs"),
                     new HttpRequestMessage(HttpMethod.Post, "/api/runs/work-items/42")
                 })
        {
            using (request)
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("ProfileNotConfigured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }

        Assert.Equal(0, source.Calls);
        Assert.Equal(0, ai.Calls);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.False(await new SqliteSingletonProfileRepository(fixture.DatabasePath).ExistsAsync());
    }

    [Fact]
    public async Task CFG_008_CorruptSQLiteProfileFailsStartupInsteadOfFallingBackToNamedYaml()
    {
        var root = RepositoryRoot();
        var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-corrupt-startup-{Guid.NewGuid():N}.db");
        await new SqliteDatabaseMigrator(databasePath).MigrateAsync();
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO singleton_profile_configuration
                    (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
                VALUES (1, 'corrupt', '{bad-json', '{}', '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-corrupt-startup-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        try
        {
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath },
                Profile = new { ConfigurationPath = Path.Combine(root, "profiles", "example", "profile.yaml") }
            }));
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            await using var factory = new WebApplicationFactory<Program>();

            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Equal("corrupt", await ReadProfileIdAsync(databasePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previous);
            SqliteConnection.ClearAllPools();
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task<string> ReadProfileIdAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_id FROM singleton_profile_configuration;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class RecordingWorkItemSource : IWorkItemSource
    {
        public int Calls { get; private set; }
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Profileless startup must not query ADO.");
        }
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Profileless startup must not read ADO.");
        }
    }

    private sealed class RecordingAiProvider : IIntakeAiProvider
    {
        public int Calls { get; private set; }
        public Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Profileless startup must not call AI.");
        }
    }

    private sealed class RecordingManagementClientFactory : IAzureDevOpsManagementClientFactory
    {
        public int ConnectionCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public IAzureDevOpsManagementClient Create(
            AzureDevOpsConfiguration configuration,
            SecretValue credential,
            int maximumRetries) => new Client(this);

        private sealed class Client(RecordingManagementClientFactory owner) : IAzureDevOpsManagementClient
        {
            public Task<AzureDevOpsConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            {
                owner.ConnectionCalls++;
                return Task.FromResult(new AzureDevOpsConnectionResult(true));
            }

            public Task<AzureDevOpsQueryValidationResult> ValidateSavedQueryAsync(
                Guid queryId, CancellationToken cancellationToken = default)
            {
                owner.QueryCalls++;
                return Task.FromResult(new AzureDevOpsQueryValidationResult(true, queryId, 0, []));
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string keyPath;
        private readonly string? previous;
        private readonly WebApplicationFactory<Program> factory;

        private Fixture(string overridePath, string databasePath, string keyPath, string? previous,
            WebApplicationFactory<Program> factory)
        {
            this.overridePath = overridePath;
            this.keyPath = keyPath;
            DatabasePath = databasePath;
            this.previous = previous;
            this.factory = factory;
            Client = factory.CreateClient();
        }

        public string DatabasePath { get; }
        public HttpClient Client { get; }
        public WebApplicationFactory<Program> Factory => factory;

        public static async Task<Fixture> CreateAsync<T>(T configuration,
            Action<IServiceCollection, string> configureServices)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-profileless-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-profileless-{Guid.NewGuid():N}.json");
            var keyPath = Path.Combine(Path.GetTempPath(), $"intake-gate-profileless-{Guid.NewGuid():N}.key");
            var json = JsonSerializer.Serialize(configuration).Replace(
                "\"Path\":\"\"", $"\"Path\":{JsonSerializer.Serialize(databasePath)}", StringComparison.Ordinal);
            await File.WriteAllTextAsync(overridePath, json);
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("SecretStore:KeyPath", keyPath);
                builder.ConfigureServices(services => configureServices(services, databasePath));
            });
            return new Fixture(overridePath, databasePath, keyPath, previous, factory);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previous);
            SqliteConnection.ClearAllPools();
            File.Delete(overridePath);
            File.Delete(keyPath);
            File.Delete(DatabasePath);
            File.Delete(DatabasePath + "-shm");
            File.Delete(DatabasePath + "-wal");
        }
    }

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
}

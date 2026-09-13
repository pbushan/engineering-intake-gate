using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class AzureDevOpsManagementEndpointTests
{
    private const string PatCanary = "phase3a-synthetic-pat-canary-value";
    private static readonly Guid NewQuery = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task DISC_009_SEC_003_AdminFlowNeverRedisplaysSecretAndActivatesWithoutRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);

        using var replace = await client.PutAsJsonAsync("/api/ado/credential/local", new { replacement = PatCanary });
        replace.EnsureSuccessStatusCode();
        var replaceBody = await replace.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PatCanary, replaceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", replaceBody, StringComparison.OrdinalIgnoreCase);

        using var connection = await client.PostAsync("/api/ado/connection-tests", null);
        connection.EnsureSuccessStatusCode();
        var metadata = await client.GetFromJsonAsync<JsonElement>("/api/ado/credential");
        Assert.Equal("verified", metadata.GetProperty("verificationStatus").GetString());

        var queryUrl = $"https://dev.azure.com/example-organization/ExampleProject/_queries/query/{NewQuery:D}/";
        using var validate = await client.PostAsJsonAsync("/api/ado/query-candidates/validate", new
        {
            savedQuery = queryUrl
        });
        validate.EnsureSuccessStatusCode();
        var candidateBody = await validate.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PatCanary, candidateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("preview-secret-canary", candidateBody, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_SECRET]", candidateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("description", candidateBody, StringComparison.OrdinalIgnoreCase);
        var candidate = JsonDocument.Parse(candidateBody).RootElement;
        Assert.Equal(12, candidate.GetProperty("totalCount").GetInt32());
        Assert.Equal(10, candidate.GetProperty("preview").GetArrayLength());

        await ExecuteAsync(fixture.DatabasePath, """
            INSERT INTO discovery_checkpoints VALUES ('example', '2026-09-12T12:00:00Z', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
            INSERT INTO discovered_work_registrations VALUES ('example', 42, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '2026-09-12T12:00:00Z', 'NewlyDiscovered', 'Completed', '2026-09-12T12:00:00Z');
            """);
        using var confirm = await client.PostAsJsonAsync("/api/ado/query-candidates/confirm", new
        {
            confirmationToken = candidate.GetProperty("confirmationToken").GetString()
        });
        confirm.EnsureSuccessStatusCode();
        var confirmed = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync()).RootElement;
        Assert.False(confirmed.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath, "SELECT COUNT(*) FROM discovery_checkpoints;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath, "SELECT COUNT(*) FROM discovered_work_registrations;"));
        Assert.True(fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>()
            .RuntimeActivationCurrent);

        var databaseBytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.DoesNotContain(PatCanary, databaseBytes, StringComparison.Ordinal);
        var audit = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(operation || changed_fields_json, ',') FROM control_plane_audits;");
        Assert.DoesNotContain(PatCanary, audit, StringComparison.Ordinal);
        Assert.Contains("AzureDevOpsDiscoveryRebaselined", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUTH_005_AUTH_008_ViewerAnonymousAndMissingCsrfCannotMutateAdoManagement()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var admin = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(admin);
        using (var created = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "ado-viewer",
            password = "viewer-deterministic-password",
            role = "viewer"
        }))
            created.EnsureSuccessStatusCode();

        using var noCsrf = fixture.Factory.CreateClient();
        await LoginAsync(noCsrf, "acceptance-admin", AuthenticatedTestClient.AdminPassword);
        noCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await noCsrf.PutAsJsonAsync("/api/ado/credential/local", new { replacement = "blocked-canary" })).StatusCode);

        using var viewer = fixture.Factory.CreateClient();
        await LoginAsync(viewer, "ado-viewer", "viewer-deterministic-password");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/ado/credential")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/ado/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync("/api/ado/credential/environment", new { environmentVariableName = "ADO_OTHER" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/ado/query-candidates/validate", new { savedQuery = NewQuery })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/ado/query-candidates/confirm", new { confirmationToken = "tampered" })).StatusCode);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ado/credential")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ado/settings")).StatusCode);
    }

    [Fact]
    public async Task DISC_009_WiqlTamperedAndStaleCandidatesCannotBecomeAuthoritative()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await client.PutAsJsonAsync("/api/ado/credential/local", new { replacement = PatCanary });

        using var wiql = await client.PostAsJsonAsync("/api/ado/query-candidates/validate", new
        {
            savedQuery = "Select [System.Id] From WorkItems"
        });
        Assert.Equal(HttpStatusCode.BadRequest, wiql.StatusCode);
        using var tampered = await client.PostAsJsonAsync("/api/ado/query-candidates/confirm", new
        {
            confirmationToken = "not-server-issued"
        });
        Assert.Equal(HttpStatusCode.Conflict, tampered.StatusCode);

        var candidate = await client.PostAsJsonAsync("/api/ado/query-candidates/validate", new { savedQuery = NewQuery });
        candidate.EnsureSuccessStatusCode();
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmationToken").GetString();
        await client.PutAsJsonAsync("/api/ado/credential/environment", new { environmentVariableName = "INTAKE_ADO_PAT" });
        using var stale = await client.PostAsJsonAsync("/api/ado/query-candidates/confirm", new { confirmationToken = token });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("11111111-1111-1111-1111-111111111111", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT json_extract(profile_json, '$.ado.savedQueryId') FROM singleton_profile_configuration;"));
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string keyPath;
        private readonly string? priorConfig;
        private readonly string? priorPat;

        private Fixture(string databasePath, string overridePath, string keyPath,
            string? priorConfig, string? priorPat, WebApplicationFactory<Program> factory)
        {
            DatabasePath = databasePath;
            this.overridePath = overridePath;
            this.keyPath = keyPath;
            this.priorConfig = priorConfig;
            this.priorPat = priorPat;
            Factory = factory;
        }

        public string DatabasePath { get; }
        public WebApplicationFactory<Program> Factory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = RepositoryRoot();
            var directory = Path.GetTempPath();
            var database = Path.Combine(directory, $"intake-gate-phase3a-{Guid.NewGuid():N}.db");
            var settings = Path.Combine(directory, $"intake-gate-phase3a-{Guid.NewGuid():N}.json");
            var key = Path.Combine(directory, $"intake-gate-phase3a-{Guid.NewGuid():N}.key");
            await LegacyProfileTestSeeder.ImportAsync(database,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = database },
                SecretStore = new { KeyPath = key }
            }));
            var priorConfig = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            var priorPat = Environment.GetEnvironmentVariable("INTAKE_ADO_PAT");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", settings);
            Environment.SetEnvironmentVariable("INTAKE_ADO_PAT", "initial-environment-pat");
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAzureDevOpsManagementClientFactory>();
                    services.AddSingleton<IAzureDevOpsManagementClientFactory, FakeManagementClientFactory>();
                });
            });
            return new Fixture(database, settings, key, priorConfig, priorPat, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", priorConfig);
            Environment.SetEnvironmentVariable("INTAKE_ADO_PAT", priorPat);
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { overridePath, keyPath, DatabasePath, DatabasePath + "-shm", DatabasePath + "-wal" })
                File.Delete(path);
        }
    }

    private sealed class FakeManagementClientFactory : IAzureDevOpsManagementClientFactory
    {
        public IAzureDevOpsManagementClient Create(
            AzureDevOpsConfiguration configuration,
            SecretValue credential,
            int maximumRetries) =>
            new FakeManagementClient(configuration);
    }

    private sealed class FakeManagementClient(AzureDevOpsConfiguration configuration) : IAzureDevOpsManagementClient
    {
        public Task<AzureDevOpsConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureDevOpsConnectionResult(true));

        public Task<AzureDevOpsQueryValidationResult> ValidateSavedQueryAsync(Guid queryId, CancellationToken cancellationToken = default)
        {
            var preview = Enumerable.Range(1, 10).Select(id => new AzureDevOpsQueryPreviewItem(
                id, id == 1 ? "password=preview-secret-canary" : $"Safe title {id}", "Bug", "New",
                new Uri(configuration.OrganizationUrl.AbsoluteUri.TrimEnd('/') + $"/{configuration.Project}/_workitems/edit/{id}")))
                .ToArray();
            return Task.FromResult(new AzureDevOpsQueryValidationResult(true, queryId, 12, preview));
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
        throw new DirectoryNotFoundException();
    }
}

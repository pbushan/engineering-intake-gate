using System.Net;
using System.Text.Json;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class ManualWorkItemEndpointTests
{
    [Fact]
    public async Task MAN_002_AC_16_EndpointEvaluatesEligibleItemAndReturnsOnlySafeDryRunSummary()
    {
        const string secret = "SYNTH_ENDPOINT_ADO_SECRET_808";
        await using var fixture = await Fixture.CreateAsync(new FakeSource([42], new RawWorkItem(
            "42", "31", "Generic", "Title", description: $"password={secret}")));

        using var response = await fixture.Client.PostAsync("/api/runs/work-items/42", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(body).RootElement;

        Assert.Equal("eligible", result.GetProperty("eligibility").GetString());
        Assert.Equal("31", result.GetProperty("evaluatedRevision").GetString());
        Assert.Equal("completed", result.GetProperty("processingStatus").GetString());
        Assert.Equal("pass", result.GetProperty("decision").GetString());
        Assert.Equal("dryRun", result.GetProperty("executionMode").GetString());
        Assert.Equal(2, result.GetProperty("proposedMutationCount").GetInt32());
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MAN_003_AC_24_EndpointCannotBypassQueryAndInvalidIdIsRejected()
    {
        var source = new FakeSource([41], new RawWorkItem("42", "1", "Generic", "Title"));
        await using var fixture = await Fixture.CreateAsync(source);

        using var outside = await fixture.Client.PostAsync("/api/runs/work-items/42", null);
        var body = JsonDocument.Parse(await outside.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("notEligible", body.GetProperty("eligibility").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("decision").ValueKind);
        Assert.Equal(0, body.GetProperty("proposedMutationCount").GetInt32());
        Assert.Equal(0, source.ReadCalls);

        using var invalid = await fixture.Client.PostAsync("/api/runs/work-items/0", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class FakeSource(IReadOnlyList<int> ids, RawWorkItem item) : IWorkItemSource
    {
        public int ReadCalls { get; private set; }
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemQueryResult(ids));
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(new WorkItemReadResult(item));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string databasePath;
        private readonly string? previousOverride;
        private readonly WebApplicationFactory<Program> factory;

        private Fixture(string overridePath, string databasePath, string? previousOverride, WebApplicationFactory<Program> factory)
        {
            this.overridePath = overridePath;
            this.databasePath = databasePath;
            this.previousOverride = previousOverride;
            this.factory = factory;
            Client = factory.CreateClient();
        }

        public HttpClient Client { get; }

        public static async Task<Fixture> CreateAsync(IWorkItemSource source)
        {
            var root = FindRepositoryRoot();
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase8-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase8-{Guid.NewGuid():N}.json");
            await LegacyProfileTestSeeder.ImportAsync(databasePath,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWorkItemSource>();
                    services.AddSingleton(source);
                });
            });
            var fixture = new Fixture(overridePath, databasePath, previous, factory);
            await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Client);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EngineeringIntakeGate.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException();
        }
    }
}

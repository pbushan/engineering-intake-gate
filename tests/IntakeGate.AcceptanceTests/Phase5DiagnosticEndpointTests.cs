using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class Phase5DiagnosticEndpointTests
{
    [Fact]
    public async Task DRY_001_DRY_002_DRY_003_DRY_004_NFR_007_FullDryRunPersistsReconstructableAuditAcrossRestart()
    {
        await using var fixture = await RestartableHostFixture.CreateAsync("Development");
        const string secret = "SYNTH_PHASE5_SECRET_701";
        var raw = new RawWorkItem("fixture-701", "23", "Generic Request", "Safe title",
            description: $"Useful evidence password={secret}", tags: ["ExistingTag"]);
        var originalTags = raw.Tags.ToArray();

        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/runs/pass", raw);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(body).RootElement;
        var runId = result.GetProperty("runId").GetGuid();
        var evaluationId = result.GetProperty("evaluationId").GetString()!;

        Assert.Equal("pass", result.GetProperty("decision").GetString());
        Assert.Equal("completed", result.GetProperty("processingStatus").GetString());
        Assert.Equal("dryRun", result.GetProperty("executionMode").GetString());
        Assert.Equal(["addTag", "postComment"], result.GetProperty("proposedMutations").EnumerateArray()
            .Select(item => item.GetProperty("type").GetString()!).ToArray());
        Assert.Empty(result.GetProperty("attemptedMutations").EnumerateArray());
        Assert.Empty(result.GetProperty("mutationOutcomes").EnumerateArray());
        var comment = result.GetProperty("proposedMutations")[1].GetProperty("body").GetString()!;
        Assert.Contains("Engineering Intake Summary", comment, StringComparison.Ordinal);
        Assert.Contains($"evaluationId={evaluationId} -->", comment, StringComparison.Ordinal);
        Assert.Equal(originalTags, raw.Tags);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);

        var persistedBeforeRestart = await fixture.Client.GetStringAsync($"/api/diagnostics/runs/{runId:D}");
        AssertPersistedAudit(persistedBeforeRestart, evaluationId);
        Assert.DoesNotContain(secret, persistedBeforeRestart, StringComparison.Ordinal);

        await fixture.RestartAsync();
        var persistedAfterRestart = await fixture.Client.GetStringAsync($"/api/diagnostics/runs/{runId:D}");
        Assert.Equal(persistedBeforeRestart, persistedAfterRestart);
        var databaseBytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.DoesNotContain(secret, databaseBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Useful evidence", databaseBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Safe title", databaseBytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SAFE_001_AUD_001_EvaluationErrorIsPersistedWithZeroMutations()
    {
        await using var fixture = await RestartableHostFixture.CreateAsync("Development");
        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/runs/e20",
            new RawWorkItem("fixture-error", "4", "Generic", "Title"));
        response.EnsureSuccessStatusCode();
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("error", result.GetProperty("processingStatus").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("decision").ValueKind);
        Assert.Empty(result.GetProperty("proposedMutations").EnumerateArray());
        var persisted = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/api/diagnostics/runs/{result.GetProperty("runId").GetGuid():D}")).RootElement;
        var evaluation = persisted.GetProperty("evaluation");
        Assert.Equal("error", evaluation.GetProperty("processingStatus").GetString());
        Assert.Equal(JsonValueKind.Null, evaluation.GetProperty("decision").ValueKind);
        Assert.Empty(evaluation.GetProperty("proposedMutations").EnumerateArray());
        Assert.Empty(evaluation.GetProperty("attemptedMutations").EnumerateArray());
        Assert.Empty(evaluation.GetProperty("mutationOutcomes").EnumerateArray());
        Assert.NotEmpty(evaluation.GetProperty("errorCategories").EnumerateArray());
        Assert.DoesNotContain("{not-json", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NFR_006_Phase5DiagnosticsAreUnavailableInProduction()
    {
        await using var fixture = await RestartableHostFixture.CreateAsync("Production");
        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/runs/pass",
            new RawWorkItem("1", "1", "Generic", "Title"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void AssertPersistedAudit(string json, string evaluationId)
    {
        var root = JsonDocument.Parse(json).RootElement;
        var run = root.GetProperty("run");
        var evaluation = root.GetProperty("evaluation");
        Assert.Equal(evaluationId, evaluation.GetProperty("evaluationId").GetString());
        Assert.Equal("fixture-701", evaluation.GetProperty("workItemId").GetString());
        Assert.Equal("23", evaluation.GetProperty("evaluatedRevision").GetString());
        Assert.Equal("example", evaluation.GetProperty("profileId").GetString());
        Assert.Equal("example-engineering-intake", evaluation.GetProperty("policyId").GetString());
        Assert.Equal("1", evaluation.GetProperty("policyVersion").GetString());
        Assert.StartsWith("sha256:", evaluation.GetProperty("policyFingerprint").GetString(), StringComparison.Ordinal);
        Assert.Equal("openai", evaluation.GetProperty("providerIdentifier").GetString());
        Assert.Equal("example-model", evaluation.GetProperty("modelIdentifier").GetString());
        Assert.Equal("intake-evaluator-v1", evaluation.GetProperty("promptVersion").GetString());
        Assert.Equal("pass", evaluation.GetProperty("decision").GetString());
        Assert.NotEmpty(evaluation.GetProperty("applicableCriteria").EnumerateArray());
        Assert.NotEmpty(evaluation.GetProperty("satisfiedCriteria").EnumerateArray());
        Assert.False(evaluation.GetProperty("attachmentContentInspected").GetBoolean());
        Assert.True(evaluation.GetProperty("redactionOccurred").GetBoolean());
        Assert.Equal(1, evaluation.GetProperty("redactionCount").GetInt32());
        Assert.Equal("dryRun", evaluation.GetProperty("executionMode").GetString());
        Assert.Equal("completed", evaluation.GetProperty("processingStatus").GetString());
        Assert.Equal("manualWorkItem", run.GetProperty("triggerType").GetString());
        Assert.Equal(1, run.GetProperty("passCount").GetInt32());
        Assert.Empty(evaluation.GetProperty("attemptedMutations").EnumerateArray());
        Assert.Empty(evaluation.GetProperty("mutationOutcomes").EnumerateArray());
    }

    private sealed class RestartableHostFixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string? previousOverride;
        private readonly string environment;
        private WebApplicationFactory<Program> factory;

        private RestartableHostFixture(string overridePath, string databasePath, string? previousOverride, string environment)
        {
            this.overridePath = overridePath;
            DatabasePath = databasePath;
            this.previousOverride = previousOverride;
            this.environment = environment;
            factory = CreateFactory(environment);
            Client = factory.CreateClient();
        }

        public string DatabasePath { get; }
        public HttpClient Client { get; private set; }

        public static async Task<RestartableHostFixture> CreateAsync(string environment)
        {
            var root = FindRepositoryRoot();
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase5-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase5-{Guid.NewGuid():N}.json");
            await LegacyProfileTestSeeder.ImportAsync(databasePath,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var fixture = new RestartableHostFixture(overridePath, databasePath, previous, environment);
            await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Client);
            return fixture;
        }

        public async Task RestartAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            factory = CreateFactory(environment);
            Client = factory.CreateClient();
            await AuthenticatedTestClient.AuthenticateAdminAsync(Client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            File.Delete(overridePath);
            File.Delete(DatabasePath);
            File.Delete(DatabasePath + "-shm");
            File.Delete(DatabasePath + "-wal");
        }

        private static WebApplicationFactory<Program> CreateFactory(string environment) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment(environment));

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
}

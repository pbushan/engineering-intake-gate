using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class OperationalEndpointTests
{
    [Fact]
    public async Task OPS_001_OPS_002_OPS_003_AdminExecutionIsGovernedModeLessActorAttributedAndCsrfProtected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.Admin;

        using var numeric = await admin.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 42 });
        numeric.EnsureSuccessStatusCode();
        var numericBody = JsonDocument.Parse(await numeric.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("pass", numericBody.GetProperty("decision").GetString());
        Assert.Equal("Engineering Ready", numericBody.GetProperty("decisionLabel").GetString());
        Assert.Equal("dryRun", numericBody.GetProperty("effectiveMode").GetString());
        Assert.True(numericBody.GetProperty("configurationGenerationId").GetInt64() > 0);

        using var url = await admin.PostAsJsonAsync("/api/runs/work-items", new
        {
            workItemUrl = "https://dev.azure.com/example-organization/ExampleProject/_workitems/edit/42"
        });
        url.EnsureSuccessStatusCode();
        using var outside = await admin.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 99 });
        outside.EnsureSuccessStatusCode();
        Assert.Equal("notEligible", JsonDocument.Parse(await outside.Content.ReadAsStringAsync()).RootElement
            .GetProperty("decision").GetString());
        Assert.Equal(2, fixture.Source.ReadCalls);

        using var unrelated = await admin.PostAsJsonAsync("/api/runs/work-items", new
        {
            workItemUrl = "https://example.invalid/ExampleProject/_workitems/edit/42"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unrelated.StatusCode);
        using var escalation = await admin.PostAsJsonAsync("/api/runs/work-items", new
        {
            workItemId = 42,
            mode = "production"
        });
        Assert.Equal(HttpStatusCode.BadRequest, escalation.StatusCode);

        using var noCsrf = fixture.Factory.CreateClient();
        await LoginAsync(noCsrf, "acceptance-admin", AuthenticatedTestClient.AdminPassword, refreshCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await noCsrf.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 42 })).StatusCode);
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 42 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/runs", null)).StatusCode);

        var leases = fixture.Factory.Services.GetRequiredService<IIncrementalDiscoveryRepository>();
        var state = fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>();
        var leaseId = Guid.NewGuid();
        var profileId = state.ActiveGeneration!.ProfileId;
        Assert.NotNull(await leases.TryAcquireRunLeaseAsync(
            profileId, leaseId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)));
        try
        {
            using var conflict = await admin.PostAsync("/api/runs", null);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Contains("ActiveRunInProgress", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await leases.ReleaseRunLeaseAsync(profileId, leaseId);
        }
    }

    [Fact]
    public async Task OPS_004_OPS_005_OPS_006_OPS_008_RunHistoryAndDetailsArePagedTypedAndSafeForViewer()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var manual = await fixture.Admin.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 42 });
        manual.EnsureSuccessStatusCode();
        var manualBody = JsonDocument.Parse(await manual.Content.ReadAsStringAsync()).RootElement.Clone();

        using var runNow = await fixture.Admin.PostAsync("/api/runs", null);
        runNow.EnsureSuccessStatusCode();
        var runNowBody = JsonDocument.Parse(await runNow.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.Equal("manualIncremental", runNowBody.GetProperty("invocationType").GetString());

        using var pageResponse = await fixture.Viewer.GetAsync("/api/runs?page=1&pageSize=1");
        var pageText = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(pageResponse.IsSuccessStatusCode, $"{pageResponse.StatusCode}: {pageText}");
        var page = JsonDocument.Parse(pageText).RootElement;
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        var newest = page.GetProperty("items")[0];
        Assert.Equal(runNowBody.GetProperty("runId").GetGuid(), newest.GetProperty("runId").GetGuid());
        Assert.Equal("acceptance-admin", newest.GetProperty("triggeredBy").GetProperty("username").GetString());
        Assert.Equal(1, newest.GetProperty("engineeringReadyCount").GetInt32());
        Assert.Equal(0, newest.GetProperty("tokenUsage").GetProperty("totalTokens").GetInt32());
        Assert.Equal(0m, newest.GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        Assert.Equal(0, newest.GetProperty("estimatedCost").GetProperty("totalInteractions").GetInt32());

        var parentId = runNowBody.GetProperty("runId").GetGuid();
        using var detailResponse = await fixture.Viewer.GetAsync($"/api/runs/{parentId:D}");
        detailResponse.EnsureSuccessStatusCode();
        var detailText = await detailResponse.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(detailText).RootElement;
        Assert.True(detail.GetProperty("summary").GetProperty("configurationGenerationId").GetInt64() > 0);
        var item = Assert.Single(detail.GetProperty("items").EnumerateArray());
        Assert.Equal("pass", item.GetProperty("decision").GetString());
        Assert.Equal("https://dev.azure.com/example-organization/ExampleProject/_workitems/edit/42",
            item.GetProperty("azureDevOpsUrl").GetString());

        var evaluationId = item.GetProperty("evaluationId").GetString();
        using var itemResponse = await fixture.Viewer.GetAsync($"/api/runs/{parentId:D}/items/{evaluationId}");
        itemResponse.EnsureSuccessStatusCode();
        var itemText = await itemResponse.Content.ReadAsStringAsync();
        var itemDetail = JsonDocument.Parse(itemText).RootElement;
        Assert.Equal(2, itemDetail.GetProperty("proposedEffects").GetArrayLength());
        Assert.Equal(0, itemDetail.GetProperty("actualEffects").GetArrayLength());
        Assert.Equal(0, itemDetail.GetProperty("tokenUsage").GetProperty("totalTokens").GetInt32());
        Assert.True(itemDetail.GetProperty("ai").GetProperty("evaluationReused").GetBoolean());
        Assert.True(itemDetail.GetProperty("reusableEvidenceAvailable").GetBoolean());
        Assert.DoesNotContain("structuredPayload", itemText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentBytes", itemText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", itemText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt\"", itemText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Viewer.GetAsync($"/api/runs/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Viewer.PostAsync("/api/runs", null)).StatusCode);
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/runs/{parentId:D}/items/{evaluationId}/screenshots/{new string('a', 64)}")).StatusCode);

        using var manualDetail = await fixture.Viewer.GetAsync(
            manualBody.GetProperty("detailUrl").GetString()!);
        manualDetail.EnsureSuccessStatusCode();

        using var healthResponse = await fixture.Viewer.GetAsync("/api/support/health");
        healthResponse.EnsureSuccessStatusCode();
        var health = await healthResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active", health.GetProperty("runtime").GetProperty("status").GetString());
        Assert.True(health.GetProperty("runtime").GetProperty("activeGenerationId").GetInt64() > 0);
        Assert.Equal("manualOnly", health.GetProperty("scheduler").GetProperty("status").GetString());
        Assert.True(health.GetProperty("recentFailures").GetArrayLength() <= 10);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("aiDiagnostics").ValueKind);

        var adminHealth = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/support/health");
        Assert.Equal(JsonValueKind.Object, adminHealth.GetProperty("aiDiagnostics").ValueKind);
        Assert.Equal("openai", adminHealth.GetProperty("aiDiagnostics")
            .GetProperty("activeRuntimeProvider").GetString());

        var runtimeState = fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>();
        var activeGeneration = runtimeState.ActiveGeneration!;
        runtimeState.MarkActivationPending();
        var activationFailure = await fixture.Viewer.GetFromJsonAsync<JsonElement>("/api/support/health");
        Assert.Equal("activationFailed", activationFailure.GetProperty("runtime").GetProperty("status").GetString());
        Assert.Equal("activationFailed", activationFailure.GetProperty("scheduler").GetProperty("status").GetString());
        runtimeState.Activate(activeGeneration);

        using var auditResponse = await fixture.Viewer.GetAsync("/api/audit?page=1&pageSize=2");
        auditResponse.EnsureSuccessStatusCode();
        var audit = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(audit.GetProperty("totalCount").GetInt32() > 0);
        Assert.InRange(audit.GetProperty("items").GetArrayLength(), 1, 2);
    }

    [Fact]
    public async Task UI_021_UI_023_RecentFailuresAreBoundedAndMissingAuditActorsRemainUnknown()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 11; index++)
        {
            using var failure = await fixture.Admin.PostAsJsonAsync("/api/diagnostics/runs/e20",
                new RawWorkItem($"support-failure-{index}", "1", "Generic", "Support failure"));
            failure.EnsureSuccessStatusCode();
        }

        var health = await fixture.Viewer.GetFromJsonAsync<JsonElement>("/api/support/health");
        Assert.Equal(10, health.GetProperty("recentFailures").GetArrayLength());
        Assert.All(health.GetProperty("recentFailures").EnumerateArray(),
            failure => Assert.Equal("error", failure.GetProperty("status").GetString()));

        var unknownActorId = Guid.NewGuid();
        var auditWriter = fixture.Factory.Services.GetRequiredService<IControlPlaneAuditWriter>();
        await auditWriter.AppendAsync(new ControlPlaneAuditRecord(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new AuditActor(unknownActorId, string.Empty), "HistoricalOperation", "Historical", "missing-actor", []));
        var audit = await fixture.Viewer.GetFromJsonAsync<JsonElement>(
            "/api/audit?page=1&pageSize=10&targetCategory=Historical&targetId=missing-actor");
        var item = Assert.Single(audit.GetProperty("items").EnumerateArray());
        Assert.Equal("unknown", item.GetProperty("actor").GetProperty("type").GetString());
        Assert.Equal("Unknown", item.GetProperty("actor").GetProperty("displayName").GetString());
        Assert.Equal(unknownActorId, item.GetProperty("actor").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task OPS_010_OpenApiContainsOperationalContractsWithoutProhibitedPayloadSchemas()
    {
        await using var fixture = await Fixture.CreateAsync();
        var document = await fixture.Admin.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/runs", document, StringComparison.Ordinal);
        Assert.Contains("RunHistoryPageResponse", document, StringComparison.Ordinal);
        Assert.Contains("RunItemDetailResponse", document, StringComparison.Ordinal);
        Assert.Contains("/api/support/health", document, StringComparison.Ordinal);
        Assert.Contains("/api/home/summary", document, StringComparison.Ordinal);
        Assert.Contains("/api/audit", document, StringComparison.Ordinal);
        Assert.Contains("SystemHealthResponse", document, StringComparison.Ordinal);
        Assert.Contains("HomeSummaryResponse", document, StringComparison.Ordinal);
        Assert.Contains("pricedInteractions", document, StringComparison.Ordinal);
        Assert.Contains("evaluationsWithPartialEstimate", document, StringComparison.Ordinal);
        Assert.Contains("ControlPlaneAuditPageResponse", document, StringComparison.Ordinal);
        Assert.DoesNotContain("EvaluationEvidence", document, StringComparison.Ordinal);
        Assert.DoesNotContain("RawWorkItem", document, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentProcessingAuditRecord", document, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderRequestIds", document, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HOME_001_HOME_005_DefaultSummaryIsViewerReadableBoundedAndProviderFree()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var executed = await fixture.Admin.PostAsJsonAsync("/api/runs/work-items", new { workItemId = 42 });
        executed.EnsureSuccessStatusCode();
        var providerCalls = fixture.Provider.CallCount;

        using var response = await fixture.Viewer.GetAsync("/api/home/summary");
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30, summary.GetProperty("windowDays").GetInt32());
        Assert.Equal(1, summary.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("engineeringReadyCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("intakeIncompleteCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("errorCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("engineeringReadyRate").GetProperty("numerator").GetInt32());
        Assert.Equal(1, summary.GetProperty("engineeringReadyRate").GetProperty("denominator").GetInt32());
        Assert.Equal(100m, summary.GetProperty("engineeringReadyRate").GetProperty("percentage").GetDecimal());
        var cost = summary.GetProperty("estimatedAiCost");
        Assert.Equal(JsonValueKind.Null, cost.GetProperty("amount").ValueKind);
        Assert.Equal(0, cost.GetProperty("evaluationsWithEstimate").GetInt32());
        Assert.Equal(1, cost.GetProperty("evaluationsWithoutEstimate").GetInt32());
        Assert.False(cost.GetProperty("complete").GetBoolean());
        Assert.InRange(summary.GetProperty("recentRuns").GetArrayLength(), 1, 8);
        Assert.Equal(providerCalls, fixture.Provider.CallCount);

        var runtimeState = fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>();
        var activeGeneration = runtimeState.ActiveGeneration!;
        runtimeState.MarkActivationPending();
        var warningSummary = await fixture.Viewer.GetFromJsonAsync<JsonElement>(
            "/api/home/summary?windowDays=30");
        Assert.Contains(warningSummary.GetProperty("healthWarnings").EnumerateArray(),
            warning => warning.GetProperty("code").GetString() == "RuntimeActivationFailed");
        Assert.Equal(providerCalls, fixture.Provider.CallCount);
        runtimeState.Activate(activeGeneration);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await fixture.Viewer.GetAsync("/api/home/summary?windowDays=14")).StatusCode);
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/home/summary?windowDays=7")).StatusCode);
    }

    [Fact]
    public async Task HOME_002_ZeroDenominatorIsUnavailableAndAllSupportedWindowsAreAccepted()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var days in new[] { 7, 30, 90 })
        {
            var summary = await fixture.Viewer.GetFromJsonAsync<JsonElement>(
                $"/api/home/summary?windowDays={days}");
            Assert.Equal(days, summary.GetProperty("windowDays").GetInt32());
            Assert.Equal(0, summary.GetProperty("engineeringReadyRate").GetProperty("denominator").GetInt32());
            Assert.Equal(JsonValueKind.Null,
                summary.GetProperty("engineeringReadyRate").GetProperty("percentage").ValueKind);
        }
    }

    private static async Task LoginAsync(HttpClient client, string username, string password, bool refreshCsrf = true)
    {
        await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        if (refreshCsrf) await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
        else client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string databasePath;
        private readonly string? previousOverride;

        private Fixture(string overridePath, string databasePath, string? previousOverride,
            WebApplicationFactory<Program> factory, FakeSource source, UsageProvider provider)
        {
            this.overridePath = overridePath;
            this.databasePath = databasePath;
            this.previousOverride = previousOverride;
            Factory = factory;
            Source = source;
            Provider = provider;
            Admin = factory.CreateClient();
            Viewer = factory.CreateClient();
        }

        public WebApplicationFactory<Program> Factory { get; }
        public FakeSource Source { get; }
        public UsageProvider Provider { get; }
        public HttpClient Admin { get; }
        public HttpClient Viewer { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = RepositoryRoot();
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase5a-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-phase5a-{Guid.NewGuid():N}.json");
            await LegacyProfileTestSeeder.ImportAsync(databasePath,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var source = new FakeSource();
            var provider = new UsageProvider();
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWorkItemSource>();
                    services.AddSingleton<IWorkItemSource>(source);
                    services.RemoveAll<IIntakeAiProvider>();
                    services.AddSingleton<IIntakeAiProvider>(provider);
                });
            });
            var fixture = new Fixture(overridePath, databasePath, previous, factory, source, provider);
            await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Admin);
            using (var createViewer = await fixture.Admin.PostAsJsonAsync("/api/users", new
            {
                username = "phase5a-viewer",
                displayName = "Phase 5A Viewer",
                password = "deterministic-viewer-password",
                role = "viewer"
            })) createViewer.EnsureSuccessStatusCode();
            await LoginAsync(fixture.Viewer, "phase5a-viewer", "deterministic-viewer-password");
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Viewer.Dispose();
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    public sealed class FakeSource : IWorkItemSource
    {
        public int ReadCalls { get; private set; }
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemQueryResult([42]));

        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(new WorkItemReadResult(new RawWorkItem(
                workItemId.ToString(), "7", "Issue", "Safe title", description: "Safe description",
                changedAtUtc: DateTimeOffset.UtcNow)));
        }
    }

    private sealed class UsageProvider : IIntakeAiProvider
    {
        public int CallCount { get; private set; }

        public Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(FakeIntakeAiProvider.ValidPass(request) with
            {
                Metadata = new AiProviderAttemptMetadata("safe-request", "fake-model", new TokenUsage(8, 3, 11))
            });
        }
    }

    private static string RepositoryRoot()
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

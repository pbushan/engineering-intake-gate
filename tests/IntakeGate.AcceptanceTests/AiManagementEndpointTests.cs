using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class AiManagementEndpointTests
{
    private const string KeyCanary = "phase3b-synthetic-api-key-canary-value";

    [Fact]
    public async Task AI_MGMT_003_OpenAiAdminFlowIsRedactedRevisionBoundAndDynamicallyActivatesRuntime()
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: true);
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        var startup = fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>();
        Assert.Equal("example-model", startup.Configuration!.Profile.Ai.Model);

        using var replace = await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local",
            new { replacement = KeyCanary });
        replace.EnsureSuccessStatusCode();
        var replaceBody = await replace.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyCanary, replaceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", replaceBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("neverVerified", JsonDocument.Parse(replaceBody).RootElement
            .GetProperty("verificationStatus").GetString());

        (await client.PostAsync("/api/ai/providers/openai/credential-tests", null)).EnsureSuccessStatusCode();
        var discovery = await client.PostAsync("/api/ai/providers/openai/models/discover", null);
        discovery.EnsureSuccessStatusCode();
        var discoveryBody = await discovery.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyCanary, discoveryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", discoveryBody, StringComparison.OrdinalIgnoreCase);

        var candidate = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "new-openai-model" });
        candidate.EnsureSuccessStatusCode();
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        var confirm = await client.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = token });
        confirm.EnsureSuccessStatusCode();
        Assert.False((await confirm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("restartRequired").GetBoolean());

        Assert.Equal("example", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT profile_id FROM singleton_profile_configuration;"));
        Assert.Equal("new-openai-model", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT json_extract(profile_json, '$.ai.model') FROM singleton_profile_configuration;"));
        Assert.Equal("ExampleProject", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT json_extract(profile_json, '$.ado.project') FROM singleton_profile_configuration;"));
        Assert.Equal("new-openai-model", startup.Configuration!.Profile.Ai.Model);
        Assert.False(startup.RestartRequired);
        Assert.True(startup.RuntimeActivationCurrent);

        var databaseBytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.DoesNotContain(KeyCanary, databaseBytes, StringComparison.Ordinal);
        var audit = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(operation || target_id || changed_fields_json, ',') FROM control_plane_audits;");
        Assert.Contains("AiConfigurationChanged", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, audit, StringComparison.Ordinal);

        await fixture.Factory.DisposeAsync();
        await using var restarted = fixture.CreateRestartedFactory();
        using var restartedClient = restarted.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(restartedClient);
        var restartedState = restarted.Services.GetRequiredService<DeploymentConfigurationState>();
        Assert.Equal("new-openai-model", restartedState.Configuration!.Profile.Ai.Model);
        Assert.False(restartedState.AiRuntimeActivationPending);
        Assert.True(restartedState.RuntimeActivationCurrent);
    }

    [Fact]
    public async Task AI_MGMT_004_CredentialReplacementMakesCandidateStaleAndAccessIsEnforced()
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: true);
        using var admin = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(admin);
        await admin.PutAsJsonAsync("/api/ai/providers/anthropic/credential/local", new { replacement = KeyCanary });
        (await admin.PostAsync("/api/ai/providers/anthropic/credential-tests", null)).EnsureSuccessStatusCode();
        var candidate = await admin.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "anthropic", model = "claude-safe-model" });
        candidate.EnsureSuccessStatusCode();
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        await admin.PutAsJsonAsync("/api/ai/providers/anthropic/credential/environment",
            new { environmentVariableName = "ANTHROPIC_TEST_KEY" });
        var stale = await admin.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = token });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("openai", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT json_extract(profile_json, '$.ai.provider') FROM singleton_profile_configuration;"));

        await admin.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new { replacement = "openai-second-key" });
        (await admin.PostAsync("/api/ai/providers/openai/credential-tests", null)).EnsureSuccessStatusCode();
        await admin.PutAsJsonAsync("/api/ai/providers/anthropic/credential/local", new { replacement = "anthropic-second-key" });
        (await admin.PostAsync("/api/ai/providers/anthropic/credential-tests", null)).EnsureSuccessStatusCode();
        var openAiCandidate = await admin.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "openai-candidate" });
        openAiCandidate.EnsureSuccessStatusCode();
        var openAiToken = (await openAiCandidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        var anthropicCandidate = await admin.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "anthropic", model = "claude-provider-change" });
        anthropicCandidate.EnsureSuccessStatusCode();
        var anthropicToken = (await anthropicCandidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/ai/model-candidates/confirm",
                new { confirmationToken = openAiToken })).StatusCode);
        (await admin.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = anthropicToken })).EnsureSuccessStatusCode();
        Assert.Equal("anthropic", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT json_extract(profile_json, '$.ai.provider') FROM singleton_profile_configuration;"));
        Assert.Equal(2L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM credential_slots WHERE slot IN ('OpenAi', 'Anthropic');"));

        using (var created = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "ai-viewer",
            password = "viewer-deterministic-password",
            role = "viewer"
        })) created.EnsureSuccessStatusCode();
        using var viewer = fixture.Factory.CreateClient();
        await LoginAsync(viewer, "ai-viewer", "viewer-deterministic-password");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/ai/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/ai/providers/openai/credential")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync("/api/ai/providers/openai/models/discover", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new { replacement = "blocked" })).StatusCode);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ai/settings")).StatusCode);
        using var noCsrf = fixture.Factory.CreateClient();
        await LoginAsync(noCsrf, "acceptance-admin", AuthenticatedTestClient.AdminPassword);
        noCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await noCsrf.PostAsync("/api/ai/providers/openai/credential-tests", null)).StatusCode);
    }

    [Fact]
    public async Task AI_MGMT_005_ProfilelessSelectionStagesWithoutCreatingAProfile()
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: false);
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await client.PutAsJsonAsync("/api/ai/providers/anthropic/credential/local", new { replacement = KeyCanary });
        (await client.PostAsync("/api/ai/providers/anthropic/credential-tests", null)).EnsureSuccessStatusCode();
        fixture.State.EmptyDiscovery = true;
        var emptyDiscovery = await client.PostAsync("/api/ai/providers/anthropic/models/discover", null);
        emptyDiscovery.EnsureSuccessStatusCode();
        Assert.Empty((await emptyDiscovery.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("models").EnumerateArray());
        var candidate = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "anthropic", model = "claude-profileless-model" });
        candidate.EnsureSuccessStatusCode();
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        var confirm = await client.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = token });
        confirm.EnsureSuccessStatusCode();
        var confirmed = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(confirmed.GetProperty("profileConfigured").GetBoolean());
        Assert.False(confirmed.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_setup_settings WHERE provider = 'anthropic' AND model_id = 'claude-profileless-model';"));
    }

    [Fact]
    public async Task AI_MGMT_006_DiscoveryOutagePreservesConfirmedModelAndVerificationFailuresAreSafe()
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: true);
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new { replacement = KeyCanary });
        fixture.State.VerificationFailure = AiManagementFailure.AuthenticationFailed;
        var failedVerification = await client.PostAsync("/api/ai/providers/openai/credential-tests", null);
        Assert.Equal(HttpStatusCode.BadGateway, failedVerification.StatusCode);
        var failedVerificationText = await failedVerification.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyCanary, failedVerificationText, StringComparison.Ordinal);
        var failedVerificationBody = JsonDocument.Parse(failedVerificationText).RootElement;
        Assert.Equal("The AI provider rejected the stored credential. Replace or correct it, then verify again.",
            failedVerificationBody.GetProperty("fieldErrors").GetProperty("ai.credential")[0].GetString());
        var failedMetadata = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers/openai/credential");
        Assert.Equal("failed", failedMetadata.GetProperty("verificationStatus").GetString());
        Assert.Equal("authenticationRejected", failedMetadata.GetProperty("verificationDiagnostic").GetString());

        fixture.State.VerificationFailure = null;
        (await client.PostAsync("/api/ai/providers/openai/credential-tests", null)).EnsureSuccessStatusCode();
        var candidate = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "example-model" });
        candidate.EnsureSuccessStatusCode();
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        (await client.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = token })).EnsureSuccessStatusCode();
        var readyBefore = await client.GetFromJsonAsync<JsonElement>("/api/ai/settings");
        Assert.True(readyBefore.GetProperty("ready").GetBoolean());

        fixture.State.DiscoveryFailure = AiManagementFailure.ProviderUnavailable;
        var unavailable = await client.PostAsync("/api/ai/providers/openai/models/discover", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var unavailableBody = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(!unavailableBody.TryGetProperty("fieldErrors", out var unavailableFields) ||
                    unavailableFields.ValueKind == JsonValueKind.Null);
        var readyAfter = await client.GetFromJsonAsync<JsonElement>("/api/ai/settings");
        Assert.Equal("example-model", readyAfter.GetProperty("model").GetString());
        Assert.True(readyAfter.GetProperty("modelConfirmed").GetBoolean());
        Assert.True(readyAfter.GetProperty("ready").GetBoolean());

        fixture.State.WrongProvider = true;
        var mismatch = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "claude-cross-provider" });
        Assert.Equal(HttpStatusCode.BadGateway, mismatch.StatusCode);
        fixture.State.WrongProvider = false;
        fixture.State.ValidationFailure = AiManagementFailure.ModelNotFound;
        var missingModel = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "missing-model" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingModel.StatusCode);
        Assert.Equal("Choose an available model or enter another model ID, then validate again.",
            (await missingModel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fieldErrors")
            .GetProperty("ai.model")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
                new { provider = "openai", model = "invalid model id" })).StatusCode);
    }

    [Theory]
    [InlineData(AiManagementFailure.ProviderUnavailable, HttpStatusCode.ServiceUnavailable, "providerUnavailable")]
    [InlineData(AiManagementFailure.Timeout, HttpStatusCode.GatewayTimeout, "timeout")]
    [InlineData(AiManagementFailure.RateLimited, HttpStatusCode.TooManyRequests, "rateLimited")]
    public async Task AI_MGMT_007_VerificationFailuresPreserveCredentialAndReturnOnlySafeClassification(
        AiManagementFailure failure, HttpStatusCode status, string diagnostic)
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: true);
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new { replacement = KeyCanary });
        fixture.State.VerificationFailure = failure;

        var response = await client.PostAsync("/api/ai/providers/openai/credential-tests", null);

        Assert.Equal(status, response.StatusCode);
        Assert.DoesNotContain(KeyCanary, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var metadata = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers/openai/credential");
        Assert.True(metadata.GetProperty("configured").GetBoolean());
        Assert.Equal("failed", metadata.GetProperty("verificationStatus").GetString());
        Assert.Equal(diagnostic, metadata.GetProperty("verificationDiagnostic").GetString());
    }

    [Fact]
    public async Task AI_PRICING_001_PricingResolvesOnlyAfterConfirmationAndUnknownPricingIsNonBlocking()
    {
        await using var fixture = await Fixture.CreateAsync(profileConfigured: false);
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local",
            new { replacement = KeyCanary });
        (await client.PostAsync("/api/ai/providers/openai/credential-tests", null)).EnsureSuccessStatusCode();

        var candidate = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "gpt-5.6-sol" });
        candidate.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/ai/pricing")).StatusCode);
        var token = (await candidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        (await client.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = token })).EnsureSuccessStatusCode();

        var pricingResponse = await client.GetAsync("/api/ai/pricing");
        pricingResponse.EnsureSuccessStatusCode();
        var pricing = await pricingResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(pricing.GetProperty("available").GetBoolean());
        Assert.Equal(4m, pricing.GetProperty("inputPerMillionTokens").GetDecimal());
        Assert.Equal(0.4m, pricing.GetProperty("cachedInputPerMillionTokens").GetDecimal());
        Assert.Equal(20m, pricing.GetProperty("outputPerMillionTokens").GetDecimal());
        Assert.Equal("USD", pricing.GetProperty("currency").GetString());
        Assert.False(pricing.GetProperty("stale").GetBoolean());
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_model_pricing_cache WHERE provider = 'openai' AND model_id = 'gpt-5.6-sol';"));

        var refresh = await client.PostAsync("/api/ai/pricing/refresh", null);
        refresh.EnsureSuccessStatusCode();
        Assert.True((await refresh.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("available").GetBoolean());

        var unknownCandidate = await client.PostAsJsonAsync("/api/ai/model-candidates/validate",
            new { provider = "openai", model = "valid-but-uncatalogued-model" });
        unknownCandidate.EnsureSuccessStatusCode();
        var unknownToken = (await unknownCandidate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("confirmationToken").GetString();
        (await client.PostAsJsonAsync("/api/ai/model-candidates/confirm",
            new { confirmationToken = unknownToken })).EnsureSuccessStatusCode();
        var unavailable = await client.GetFromJsonAsync<JsonElement>("/api/ai/pricing");
        Assert.False(unavailable.GetProperty("available").GetBoolean());
        Assert.Equal("valid-but-uncatalogued-model",
            unavailable.GetProperty("model").GetString());
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_setup_settings WHERE model_id = 'valid-but-uncatalogued-model';"));
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        await AuthenticatedTestClient.RefreshCsrfTokenAsync(client);
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
        private readonly string settingsPath;
        private readonly string keyPath;
        private readonly string? priorConfig;

        private Fixture(string databasePath, string settingsPath, string keyPath,
            string? priorConfig, FakeAiManagementState state, WebApplicationFactory<Program> factory)
        {
            DatabasePath = databasePath;
            this.settingsPath = settingsPath;
            this.keyPath = keyPath;
            this.priorConfig = priorConfig;
            State = state;
            Factory = factory;
        }

        public string DatabasePath { get; }
        public FakeAiManagementState State { get; }
        public WebApplicationFactory<Program> Factory { get; }

        public static async Task<Fixture> CreateAsync(bool profileConfigured)
        {
            var database = Path.Combine(Path.GetTempPath(), $"intake-gate-phase3b-{Guid.NewGuid():N}.db");
            var settings = Path.Combine(Path.GetTempPath(), $"intake-gate-phase3b-{Guid.NewGuid():N}.json");
            var key = Path.Combine(Path.GetTempPath(), $"intake-gate-phase3b-{Guid.NewGuid():N}.key");
            if (profileConfigured)
                await LegacyProfileTestSeeder.ImportAsync(database,
                    Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = database },
                SecretStore = new { KeyPath = key }
            }));
            var priorConfig = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", settings);
            var state = new FakeAiManagementState();
            var factory = BuildFactory(state);
            return new Fixture(database, settings, key, priorConfig, state, factory);
        }

        public WebApplicationFactory<Program> CreateRestartedFactory() => BuildFactory(State);

        private static WebApplicationFactory<Program> BuildFactory(FakeAiManagementState state) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAiManagementClientFactory>();
                    services.AddSingleton<IAiManagementClientFactory>(new FakeAiManagementClientFactory(state));
                });
            });

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", priorConfig);
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { settingsPath, keyPath, DatabasePath, DatabasePath + "-shm", DatabasePath + "-wal" })
                File.Delete(path);
        }
    }

    public sealed class FakeAiManagementState
    {
        public AiManagementFailure? VerificationFailure { get; set; }
        public AiManagementFailure? DiscoveryFailure { get; set; }
        public AiManagementFailure? ValidationFailure { get; set; }
        public bool EmptyDiscovery { get; set; }
        public bool WrongProvider { get; set; }
    }

    private sealed class FakeAiManagementClientFactory(FakeAiManagementState state) : IAiManagementClientFactory
    {
        public IAiManagementClient Create(string provider, SecretValue credential, TimeSpan timeout) =>
            new FakeAiManagementClient(provider, state);
    }

    private sealed class FakeAiManagementClient(string provider, FakeAiManagementState state) : IAiManagementClient
    {
        public Task<AiCredentialVerificationResult> VerifyCredentialAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state.VerificationFailure is { } failure
                ? new AiCredentialVerificationResult(false, failure)
                : new AiCredentialVerificationResult(true));

        public Task<AiModelDiscoveryResult> DiscoverModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state.DiscoveryFailure is { } failure
                ? new AiModelDiscoveryResult(false, [], failure)
                : new AiModelDiscoveryResult(true,
                    state.EmptyDiscovery
                        ? []
                        : [new AiModelDescriptor(provider, provider == "openai" ? "new-openai-model" : "claude-safe-model", "Safe model")]));

        public Task<AiModelValidationResult> ValidateModelAsync(
            string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(state.ValidationFailure is { } failure
                ? new AiModelValidationResult(false, null, failure)
                : new AiModelValidationResult(true,
                    new AiModelDescriptor(state.WrongProvider
                        ? provider == "openai" ? "anthropic" : "openai"
                        : provider, modelId, "Safe model")));
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

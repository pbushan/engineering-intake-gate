using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ProfileManagementEndpointTests
{
    private static readonly Guid QueryId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task SETUP_4B0_DraftValidationReturnsSafeStableFieldAndSectionErrorsTogether()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var admin = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(admin);
        var defaults = await admin.GetFromJsonAsync<JsonElement>("/api/setup/defaults");
        (await admin.PostAsync("/api/setup/profile-draft/initialize", null)).EnsureSuccessStatusCode();

        var incomplete = JsonNode.Parse(defaults.GetProperty("values").GetRawText())!.AsObject();
        using var saved = await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 1,
            values = incomplete
        });
        saved.EnsureSuccessStatusCode();
        var savedBody = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Policy URL is required.", savedBody.GetProperty("fieldErrors")
            .GetProperty("policyUrl")[0].GetString());
        Assert.Equal("Add at least one intake criterion.", savedBody.GetProperty("sectionErrors")
            .GetProperty("policy.criteria")[0].GetString());
        Assert.True(savedBody.GetProperty("fieldErrors").EnumerateObject().Count() > 2);

        const string sensitiveMarker = "SYNTHETIC_SECRET_MUST_NOT_RETURN";
        incomplete["policyUrl"] = sensitiveMarker;
        incomplete["processing"]!["concurrency"] = -1;
        using var invalid = await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 2,
            values = incomplete
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidText = await invalid.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sensitiveMarker, invalidText, StringComparison.Ordinal);
        var invalidBody = JsonDocument.Parse(invalidText).RootElement;
        Assert.Equal("ValidationFailed", invalidBody.GetProperty("error").GetString());
        Assert.True(invalidBody.GetProperty("fieldErrors").TryGetProperty("policyUrl", out _));
        Assert.True(invalidBody.GetProperty("fieldErrors").TryGetProperty("processing.concurrency", out _));
        Assert.True(invalidBody.GetProperty("sectionErrors").TryGetProperty("policy.criteria", out _));
    }

    [Fact]
    public async Task SETUP_4B0_DraftDefaultsProgressAndAuthorizationAreRestartSafeSetupStateOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var admin = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(admin);

        var defaults = await admin.GetFromJsonAsync<JsonElement>("/api/setup/defaults");
        Assert.Equal(60, defaults.GetProperty("values").GetProperty("aiRuntime")
            .GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("DRY_RUN", defaults.GetProperty("values").GetProperty("processing")
            .GetProperty("executionMode").GetString());
        Assert.False(defaults.GetProperty("values").GetProperty("schedule")
            .GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("values").GetProperty("policyUrl").ValueKind);

        using var initialize = await admin.PostAsync("/api/setup/profile-draft/initialize", null);
        initialize.EnsureSuccessStatusCode();
        var initialized = await initialize.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, initialized.GetProperty("revision").GetInt32());
        using var initializeAgain = await admin.PostAsync("/api/setup/profile-draft/initialize", null);
        initializeAgain.EnsureSuccessStatusCode();
        Assert.Equal(1, (await initializeAgain.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revision").GetInt32());

        var partialValues = JsonNode.Parse(defaults.GetProperty("values").GetRawText())!.AsObject();
        partialValues["policyUrl"] = "https://example.invalid/engineering/intake-standard";
        using var partial = await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 1,
            values = partialValues
        });
        partial.EnsureSuccessStatusCode();
        Assert.Equal(2, (await partial.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revision").GetInt32());

        var unsupportedValues = JsonNode.Parse(JsonSerializer.Serialize(WriteRequest()))!.AsObject();
        unsupportedValues["unsupportedRuleLanguage"] = "field == arbitrary";
        using var unsupported = await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 2,
            values = unsupportedValues
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        using var update = await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 2,
            values = WriteRequest()
        });
        update.EnsureSuccessStatusCode();
        Assert.Equal(3, (await update.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revision").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 2,
            values = WriteRequest()
        })).StatusCode);

        using var progress = await admin.PutAsJsonAsync("/api/setup/progress", new { step = "Profile" });
        progress.EnsureSuccessStatusCode();
        Assert.Equal("Profile", (await progress.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lastVisitedStep").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/setup/progress", new { step = "ArbitraryStep" })).StatusCode);

        var profile = await admin.GetFromJsonAsync<JsonElement>("/api/profile");
        Assert.False(profile.GetProperty("exists").GetBoolean());
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(status.GetProperty("onboardingDraftExists").GetBoolean());
        Assert.True(status.GetProperty("profileDetailsComplete").GetBoolean());
        Assert.False(status.GetProperty("profileConfigured").GetBoolean());
        Assert.Equal("Profile", status.GetProperty("lastVisitedStep").GetString());
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM runtime_configuration_generations;"));

        using (var user = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "draft-viewer",
            password = "viewer-deterministic-password",
            role = "viewer"
        })) user.EnsureSuccessStatusCode();
        using var viewer = fixture.Factory.CreateClient();
        await LoginAsync(viewer, "draft-viewer", "viewer-deterministic-password");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/setup/defaults")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/setup/profile-draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync("/api/setup/profile-draft/initialize", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync("/api/setup/progress", new { step = "SavedQuery" })).StatusCode);

        var persistedJson = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT draft_json FROM onboarding_profile_draft;");
        Assert.DoesNotContain("credential", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("savedQuery", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", persistedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SETUP_4B0_FinalizationRequiresConfirmedQueryAndAtomicallyConsumesDraftAndStaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        (await client.PostAsync("/api/setup/profile-draft/initialize", null)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 1,
            values = WriteRequest()
        })).EnsureSuccessStatusCode();

        using var premature = await client.PostAsJsonAsync("/api/setup/finalize",
            new { expectedDraftRevision = 2 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, premature.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM onboarding_profile_draft;"));

        await ConfirmSetupAsync(client);
        using var clientOverride = await client.PostAsJsonAsync("/api/setup/finalize", new
        {
            expectedDraftRevision = 2,
            provider = "anthropic",
            savedQueryId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.BadRequest, clientOverride.StatusCode);
        using var competingClient = fixture.Factory.CreateClient();
        await LoginAsync(competingClient);
        var finalizations = await Task.WhenAll(
            client.PostAsJsonAsync("/api/setup/finalize", new { expectedDraftRevision = 2 }),
            competingClient.PostAsJsonAsync("/api/setup/finalize", new { expectedDraftRevision = 2 }));
        var finalized = Assert.Single(finalizations, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(finalizations, response => response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound);
        var body = await finalized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("exists").GetBoolean());
        Assert.Equal("openai", body.GetProperty("ai").GetProperty("provider").GetString());
        Assert.Equal(QueryId, body.GetProperty("azureDevOps").GetProperty("savedQueryId").GetGuid());
        Assert.Equal("active", body.GetProperty("activation").GetProperty("status").GetString());

        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM runtime_configuration_generations;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM active_runtime_configuration;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM onboarding_profile_draft;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ado_setup_settings;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_setup_settings;"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/setup/finalize",
                new { expectedDraftRevision = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync("/api/setup/profile-draft/initialize", null)).StatusCode);
        foreach (var response in finalizations) response.Dispose();
    }

    [Fact]
    public async Task SETUP_4B0_LiveFinalizationIsRejectedAndLeavesRecoverableDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        (await client.PostAsync("/api/setup/profile-draft/initialize", null)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/setup/profile-draft", new
        {
            expectedRevision = 1,
            values = WriteRequest(executionMode: "LIVE")
        })).EnsureSuccessStatusCode();
        await ConfirmSetupAsync(client);

        using var response = await client.PostAsJsonAsync("/api/setup/finalize",
            new { expectedDraftRevision = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ProductionNotAuthorized", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM onboarding_profile_draft;"));
        var audits = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(operation, ',') FROM control_plane_audits;");
        Assert.Contains("OnboardingProductionModeRejected", audits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PROFILE_001_PROFILE_002_CreateConsumesConfirmedStagingAndConcurrentCreateHasOneWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var first = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(first);
        await ConfirmSetupAsync(first);
        using var second = fixture.Factory.CreateClient();
        await LoginAsync(second);

        var requests = new[]
        {
            first.PostAsJsonAsync("/api/profile", WriteRequest()),
            second.PostAsJsonAsync("/api/profile", WriteRequest())
        };
        var responses = await Task.WhenAll(requests);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();

        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ado_configuration_state;"));
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_configuration_state;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ado_setup_settings;"));
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM ai_setup_settings;"));
        var persisted = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT profile_json FROM singleton_profile_configuration;");
        Assert.Contains("INTAKE_GATE_SECRET_SLOT_AZURE_DEVOPS", persisted, StringComparison.Ordinal);
        Assert.Contains("INTAKE_GATE_SECRET_SLOT_OPENAI", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-api-secret-canary", persisted, StringComparison.Ordinal);
        var audits = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(operation || changed_fields_json, ',') FROM control_plane_audits;");
        Assert.Contains("ProfileCreated", audits, StringComparison.Ordinal);
        Assert.Contains("ProfileSetupStagingConsumed", audits, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-api-secret-canary", audits, StringComparison.Ordinal);
        var setup = await first.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(setup.GetProperty("profileConfigured").GetBoolean());
        Assert.True(setup.GetProperty("runtimeActivationCurrent").GetBoolean());
        var runtime = fixture.Factory.Services.GetRequiredService<DeploymentConfigurationState>();
        Assert.NotNull(runtime.Configuration);
        Assert.NotNull(runtime.ActiveGeneration);
        Assert.False(runtime.RestartRequired);
        Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM runtime_configuration_generations;"));
    }

    [Fact]
    public async Task PROFILE_003_PROFILE_004_UpdateIsAtomicRevisionBoundAndCannotBypassAdoAiOrProductionGate()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        await ConfirmSetupAsync(client);
        using var create = await client.PostAsJsonAsync("/api/profile", WriteRequest());
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var profileId = created.GetProperty("profileId").GetString();
        var revision = created.GetProperty("configurationRevision").GetString();

        using var update = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = revision,
            profile = WriteRequest(expression: "0 30 * * * *", retentionDays: 120)
        });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profileId, updated.GetProperty("profileId").GetString());
        Assert.Equal(120, updated.GetProperty("audit").GetProperty("retentionDays").GetInt32());
        Assert.False(updated.GetProperty("schedule").GetProperty("activationPending").GetBoolean());
        Assert.Equal("active", updated.GetProperty("activation").GetProperty("status").GetString());
        var supportHealth = await client.GetFromJsonAsync<JsonElement>("/api/support/health");
        Assert.Equal("scheduled", supportHealth.GetProperty("scheduler").GetProperty("status").GetString());
        Assert.Equal("UTC", supportHealth.GetProperty("scheduler").GetProperty("timezone").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            supportHealth.GetProperty("scheduler").GetProperty("nextOccurrenceUtc").ValueKind);
        Assert.Equal("verified", supportHealth.GetProperty("azureDevOps").GetProperty("status").GetString());
        Assert.Equal("verified", supportHealth.GetProperty("ai").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            supportHealth.GetProperty("azureDevOps").GetProperty("lastVerifiedAtUtc").ValueKind);
        Assert.Equal("openai", supportHealth.GetProperty("aiDiagnostics")
            .GetProperty("activeRuntimeProvider").GetString());

        using var stale = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = revision,
            profile = WriteRequest(retentionDays: 121)
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var bypass = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = updated.GetProperty("configurationRevision").GetString(),
            profile = new
            {
                profileVersion = "1",
                policyUrl = "https://example.invalid/policy",
                azureDevOps = new { savedQueryId = Guid.NewGuid() },
                ai = new { provider = "anthropic", model = "unvalidated" }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bypass.StatusCode);

        using var live = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = updated.GetProperty("configurationRevision").GetString(),
            profile = WriteRequest(executionMode: "LIVE", retentionDays: 121)
        });
        Assert.Equal(HttpStatusCode.BadRequest, live.StatusCode);
        Assert.Contains("ProductionNotAuthorized", await live.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var invalidSchedule = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = updated.GetProperty("configurationRevision").GetString(),
            profile = WriteRequest(timezone: "Not/A_Timezone", retentionDays: 121)
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSchedule.StatusCode);
        var persisted = await client.GetFromJsonAsync<JsonElement>("/api/profile");
        Assert.Equal(120, persisted.GetProperty("audit").GetProperty("retentionDays").GetInt32());
        Assert.Equal(QueryId, persisted.GetProperty("azureDevOps").GetProperty("savedQueryId").GetGuid());
        Assert.Equal("openai", persisted.GetProperty("ai").GetProperty("provider").GetString());

        using var organizationChange = await client.PostAsJsonAsync("/api/ado/query-candidates/validate", new
        {
            organizationUrl = "https://dev.azure.com/a-different-organization/",
            project = "ProfileApiProject",
            savedQuery = QueryId
        });
        Assert.Equal(HttpStatusCode.BadRequest, organizationChange.StatusCode);

        (await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new
        {
            replacement = "replacement-secret-that-must-never-return"
        })).EnsureSuccessStatusCode();
        using var unverifiedCredentialSave = await client.PutAsJsonAsync("/api/profile", new
        {
            expectedRevision = updated.GetProperty("configurationRevision").GetString(),
            profile = WriteRequest(retentionDays: 121)
        });
        Assert.Equal(HttpStatusCode.Conflict, unverifiedCredentialSave.StatusCode);
        Assert.Contains("IntegrationValidationRequired",
            await unverifiedCredentialSave.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(120, (await client.GetFromJsonAsync<JsonElement>("/api/profile"))
            .GetProperty("audit").GetProperty("retentionDays").GetInt32());
        var audits = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(operation || changed_fields_json, ',') FROM control_plane_audits;");
        Assert.Contains("ProfileScheduleChanged", audits, StringComparison.Ordinal);
        Assert.Contains("ProfileProductionModeRejected", audits, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret-that-must-never-return", audits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PROFILE_005_AUTH_005_OpenApiViewerReadsCsrfAndSecretSafetyAreEnforced()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var admin = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(admin);
        using (var invalid = await admin.PostAsJsonAsync("/api/profile", WriteRequest(timezone: "Not/A_Timezone")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var incomplete = await admin.PostAsJsonAsync("/api/profile", WriteRequest()))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, incomplete.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(fixture.DatabasePath,
            "SELECT COUNT(*) FROM singleton_profile_configuration;"));
        using (var user = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "profile-viewer",
            password = "viewer-deterministic-password",
            role = "viewer"
        })) user.EnsureSuccessStatusCode();

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);

        using var noCsrf = fixture.Factory.CreateClient();
        await LoginAsync(noCsrf);
        noCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await noCsrf.PostAsJsonAsync("/api/profile", WriteRequest())).StatusCode);

        using var viewer = fixture.Factory.CreateClient();
        await LoginAsync(viewer, "profile-viewer", "viewer-deterministic-password");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/setup/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/profile/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/profile", WriteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync("/api/profile", new
            {
                expectedRevision = "sha256:stale",
                profile = WriteRequest()
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/profile/imports/legacy", new { profileYaml = "x", policyYaml = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/openapi/v1.json")).StatusCode);

        using var openApiResponse = await admin.GetAsync("/openapi/v1.json");
        openApiResponse.EnsureSuccessStatusCode();
        var openApi = await openApiResponse.Content.ReadAsStringAsync();
        foreach (var path in new[]
                 {
                     "/api/auth/session", "/api/ado/settings", "/api/ai/settings", "/api/profile",
                     "/api/profile/imports/legacy", "/api/profile/export", "/api/setup/status",
                     "/api/setup/defaults", "/api/setup/profile-draft", "/api/setup/progress",
                     "/api/setup/finalize"
                 })
            Assert.Contains(path, openApi, StringComparison.Ordinal);
        Assert.Contains("ApiErrorResponse", openApi, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", openApi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", openApi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LocalPat", openApi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", openApi, StringComparison.OrdinalIgnoreCase);

        using var runtimeDocument = JsonDocument.Parse(openApi);
        using var uiSnapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot(), "src", "IntakeGate.Web", "openapi", "intake-gate.v1.json")));
        AssertUiContractSnapshot(runtimeDocument.RootElement, uiSnapshot.RootElement);
    }

    private static void AssertUiContractSnapshot(JsonElement runtime, JsonElement snapshot)
    {
        var runtimePaths = runtime.GetProperty("paths");
        foreach (var expectedPath in snapshot.GetProperty("paths").EnumerateObject())
        {
            Assert.True(runtimePaths.TryGetProperty(expectedPath.Name, out var runtimePath),
                $"Runtime OpenAPI is missing the Phase 4A UI path {expectedPath.Name}.");
            foreach (var expectedOperation in expectedPath.Value.EnumerateObject())
            {
                Assert.True(runtimePath.TryGetProperty(expectedOperation.Name, out _),
                    $"Runtime OpenAPI is missing {expectedOperation.Name.ToUpperInvariant()} {expectedPath.Name}.");
            }
        }

        var runtimeSchemas = runtime.GetProperty("components").GetProperty("schemas");
        foreach (var expectedSchema in snapshot.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            Assert.True(runtimeSchemas.TryGetProperty(expectedSchema.Name, out var runtimeSchema),
                $"Runtime OpenAPI is missing the Phase 4A UI schema {expectedSchema.Name}.");

            if (expectedSchema.Value.TryGetProperty("required", out var expectedRequired))
            {
                var runtimeRequired = runtimeSchema.GetProperty("required").EnumerateArray()
                    .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var property in expectedRequired.EnumerateArray())
                    Assert.Contains(property.GetString(), runtimeRequired);
            }

            if (expectedSchema.Value.TryGetProperty("enum", out var expectedEnum))
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEnum.GetRawText()),
                    JsonNode.Parse(runtimeSchema.GetProperty("enum").GetRawText())),
                    $"Runtime OpenAPI enum {expectedSchema.Name} drifted from the Phase 4A UI snapshot.");

            if (!expectedSchema.Value.TryGetProperty("properties", out var expectedProperties)) continue;
            var runtimeProperties = runtimeSchema.GetProperty("properties");
            foreach (var expectedProperty in expectedProperties.EnumerateObject())
            {
                Assert.True(runtimeProperties.TryGetProperty(expectedProperty.Name, out var runtimeProperty),
                    $"Runtime OpenAPI schema {expectedSchema.Name} is missing UI property {expectedProperty.Name}.");
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedProperty.Value.GetRawText()),
                        JsonNode.Parse(runtimeProperty.GetRawText())),
                    $"Runtime OpenAPI schema {expectedSchema.Name}.{expectedProperty.Name} drifted from the Phase 4A UI snapshot.");
            }
        }
    }

    [Fact]
    public async Task PROFILE_006_LegacyContentImportPreservesIdentityRejectsPathsAndExportsNoSecrets()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        await AuthenticatedTestClient.AuthenticateAdminAsync(client);
        var root = RepositoryRoot();
        var profileYaml = await File.ReadAllTextAsync(Path.Combine(root, "profiles", "example", "profile.yaml"));
        var policyYaml = await File.ReadAllTextAsync(Path.Combine(root, "profiles", "example", "intake-policy.yaml"));

        using var pathAttempt = await client.PostAsJsonAsync("/api/profile/imports/legacy", new
        {
            profilePath = "/etc/passwd",
            policyPath = "/etc/hosts"
        });
        Assert.Equal(HttpStatusCode.BadRequest, pathAttempt.StatusCode);
        using var secretAttempt = await client.PostAsJsonAsync("/api/profile/imports/legacy", new
        {
            profileYaml = profileYaml.Replace("INTAKE_ADO_PAT", "synthetic-secret!", StringComparison.Ordinal),
            policyYaml
        });
        Assert.Equal(HttpStatusCode.BadRequest, secretAttempt.StatusCode);

        using var import = await client.PostAsJsonAsync("/api/profile/imports/legacy", new { profileYaml, policyYaml });
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var imported = await import.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("example", imported.GetProperty("profileId").GetString());
        using var duplicate = await client.PostAsJsonAsync("/api/profile/imports/legacy", new { profileYaml, policyYaml });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var export = await client.GetAsync("/api/profile/export");
        export.EnsureSuccessStatusCode();
        var body = await export.Content.ReadAsStringAsync();
        Assert.Contains("intake-gate-profile-export-v1", body, StringComparison.Ordinal);
        Assert.Contains("\"profileId\":\"example\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("authentication", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environmentVariable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INTAKE_ADO_PAT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INTAKE_AI_API_KEY", body, StringComparison.Ordinal);
        using (var user = await client.PostAsJsonAsync("/api/users", new
        {
            username = "export-viewer",
            password = "viewer-deterministic-password",
            role = "viewer"
        })) user.EnsureSuccessStatusCode();
        using var viewer = fixture.Factory.CreateClient();
        await LoginAsync(viewer, "export-viewer", "viewer-deterministic-password");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/profile/export")).StatusCode);
    }

    private static async Task ConfirmSetupAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync("/api/ado/settings", new
        {
            organizationUrl = "https://dev.azure.com/profile-api/",
            project = "ProfileApiProject"
        })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/ado/credential/local", new
        {
            replacement = "profile-api-secret-canary"
        })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/ado/connection-tests", null)).EnsureSuccessStatusCode();
        using (var validation = await client.PostAsJsonAsync("/api/ado/query-candidates/validate", new
        {
            savedQuery = QueryId
        }))
        {
            validation.EnsureSuccessStatusCode();
            var body = await validation.Content.ReadFromJsonAsync<JsonElement>();
            (await client.PostAsJsonAsync("/api/ado/query-candidates/confirm", new
            {
                confirmationToken = body.GetProperty("confirmationToken").GetString()
            })).EnsureSuccessStatusCode();
        }
        (await client.PutAsJsonAsync("/api/ai/providers/openai/credential/local", new
        {
            replacement = "profile-api-ai-secret-canary"
        })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/ai/providers/openai/credential-tests", null)).EnsureSuccessStatusCode();
        using var aiValidation = await client.PostAsJsonAsync("/api/ai/model-candidates/validate", new
        {
            provider = "openai",
            model = "profile-api-model"
        });
        aiValidation.EnsureSuccessStatusCode();
        var aiBody = await aiValidation.Content.ReadFromJsonAsync<JsonElement>();
        (await client.PostAsJsonAsync("/api/ai/model-candidates/confirm", new
        {
            confirmationToken = aiBody.GetProperty("confirmationToken").GetString()
        })).EnsureSuccessStatusCode();
    }

    private static object WriteRequest(
        string executionMode = "DRY_RUN",
        string expression = "0 0 * * * *",
        string timezone = "UTC",
        int retentionDays = 90) => new
        {
            profileVersion = "1",
            policyUrl = "https://example.invalid/engineering/intake-standard",
            intakeState = new { validatedTag = "INTAKE-VALIDATED", incompleteTag = "INTAKE-INCOMPLETE" },
            aiRuntime = new { timeoutSeconds = 60, pricing = Array.Empty<object>() },
            schedule = new { enabled = true, expression, timezone, initialLookback = "1.00:00:00" },
            processing = new
            {
                executionMode,
                concurrency = 2,
                retries = 2,
                contentLimits = new
                {
                    maximumTotalCharacters = 100000,
                    maximumComments = 100,
                    maximumExtractedTextCharacters = 75000
                },
                attachmentLimits = new
                {
                    maximumCount = 20,
                    maximumBytesPerAttachment = 10485760,
                    maximumAggregateBytes = 52428800,
                    maximumPdfPages = 200,
                    maximumImageCount = 10,
                    maximumImageBytes = 5242880,
                    maximumCsvRows = 1000,
                    maximumStructuredTextDepth = 32
                }
            },
            audit = new { retentionDays },
            exclusions = new[]
        {
            new { id = "excluded-state", field = "Generic.State", @operator = "equalsAny", values = new[] { "Excluded" } }
        },
            policy = new
            {
                id = "profile-api-policy",
                version = "1",
                criteria = new[]
            {
                new
                {
                    id = "problem_statement",
                    displayName = "Problem Statement",
                    description = "A clear description of the observed problem.",
                    applicability = "required",
                    na = new { allowed = false, requiresExplanation = false },
                    evaluationGuidance = "Determine whether the observed problem is clear."
                }
            }
            }
        };

    private static async Task LoginAsync(
        HttpClient client,
        string username = "acceptance-admin",
        string password = AuthenticatedTestClient.AdminPassword)
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
        private readonly string overridePath;
        private readonly string keyPath;
        private readonly string? priorConfiguration;

        private Fixture(string databasePath, string overridePath, string keyPath,
            string? priorConfiguration, WebApplicationFactory<Program> factory)
        {
            DatabasePath = databasePath;
            this.overridePath = overridePath;
            this.keyPath = keyPath;
            this.priorConfiguration = priorConfiguration;
            Factory = factory;
        }

        public string DatabasePath { get; }
        public WebApplicationFactory<Program> Factory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = Path.Combine(Path.GetTempPath(), $"intake-gate-profile-api-{Guid.NewGuid():N}.db");
            var settings = Path.Combine(Path.GetTempPath(), $"intake-gate-profile-api-{Guid.NewGuid():N}.json");
            var key = Path.Combine(Path.GetTempPath(), $"intake-gate-profile-api-{Guid.NewGuid():N}.key");
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = database },
                AiManagement = new { UseDeterministicFake = true }
            }));
            var prior = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", settings);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("SecretStore:KeyPath", key);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAzureDevOpsManagementClientFactory>();
                    services.AddSingleton<IAzureDevOpsManagementClientFactory, SuccessfulAdoManagementClientFactory>();
                });
            });
            _ = factory.CreateClient();
            return new Fixture(database, settings, key, prior, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", priorConfiguration);
            SqliteConnection.ClearAllPools();
            File.Delete(overridePath);
            File.Delete(keyPath);
            File.Delete(DatabasePath);
            File.Delete(DatabasePath + "-shm");
            File.Delete(DatabasePath + "-wal");
        }
    }

    private sealed class SuccessfulAdoManagementClientFactory : IAzureDevOpsManagementClientFactory
    {
        public IAzureDevOpsManagementClient Create(
            AzureDevOpsConfiguration configuration, SecretValue credential, int maximumRetries) => new Client();

        private sealed class Client : IAzureDevOpsManagementClient
        {
            public Task<AzureDevOpsConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new AzureDevOpsConnectionResult(true));

            public Task<AzureDevOpsQueryValidationResult> ValidateSavedQueryAsync(
                Guid queryId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new AzureDevOpsQueryValidationResult(true, queryId, 0, []));
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

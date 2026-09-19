using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Host;
using IntakeGate.Host.Audit;
using IntakeGate.Host.Configuration;
using IntakeGate.Host.Evidence;
using IntakeGate.Host.Evaluation;
using IntakeGate.Infrastructure.Evidence;
using IntakeGate.Infrastructure.Persistence;
using IntakeGate.Infrastructure.Configuration;
using IntakeGate.Infrastructure.Ai;
using IntakeGate.Application.WorkItems;
using IntakeGate.Application.Discovery;
using IntakeGate.Host.Discovery;
using IntakeGate.Infrastructure.AzureDevOps;
using IntakeGate.Host.WorkItems;
using IntakeGate.Host.Authentication;
using IntakeGate.Application.Authentication;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Setup;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Infrastructure.Secrets;
using IntakeGate.Host.AzureDevOps;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.AiPricing;
using IntakeGate.Host.AiManagement;
using IntakeGate.Host.Operations;
using IntakeGate.Application.Profiles;
using IntakeGate.Host.Supportability;
using IntakeGate.Host.Home;
using IntakeGate.Infrastructure.Ai.Pricing;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

var legacyImportPath = ReadLegacyImportPath(args);

if (args.Contains("--health-check", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddOpenApi();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "IntakeGate.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "IntakeGate.Authentication";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.EventsType = typeof(LocalCookieAuthenticationEvents);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(LocalAuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
    .AddPolicy(LocalAuthPolicies.Admin, policy =>
        policy.RequireAuthenticatedUser().RequireRole(LocalUserRole.Admin.ToString()));

var mountedConfigurationPath = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(mountedConfigurationPath))
{
    builder.Configuration.AddJsonFile(mountedConfigurationPath, optional: false, reloadOnChange: false);
}

builder.Services
    .AddOptions<OperationalDatabaseOptions>()
    .Bind(builder.Configuration.GetSection(OperationalDatabaseOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Path), "A database path is required.")
    .ValidateOnStart();

var databaseOptions = builder.Configuration.GetSection(OperationalDatabaseOptions.SectionName)
    .Get<OperationalDatabaseOptions>() ?? new OperationalDatabaseOptions();
if (string.IsNullOrWhiteSpace(databaseOptions.Path))
    throw new ConfigurationValidationException("OperationalDatabase:Path is required.");

var databaseMigrator = new SqliteDatabaseMigrator(databaseOptions.Path);
await databaseMigrator.MigrateAsync();
var deploymentConfigurationValidator = new DeploymentConfigurationValidator();
var singletonProfileRepository = new SqliteSingletonProfileRepository(databaseOptions.Path, deploymentConfigurationValidator);

if (legacyImportPath is not null)
{
    try
    {
        var imported = await new LegacyProfileImporter(singletonProfileRepository).ImportAsync(legacyImportPath);
        Console.WriteLine($"Legacy profile '{imported.Profile.Identity.Id}' imported into SQLite.");
        return 0;
    }
    catch (SingletonProfileAlreadyExistsException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (ConfigurationValidationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Legacy profile import failed before a profile could be activated.");
        return 2;
    }
}

var deploymentConfiguration = await singletonProfileRepository.LoadAsync();
var databasePath = Path.GetFullPath(databaseOptions.Path);
var keyPath = builder.Configuration["SecretStore:KeyPath"];
if (string.IsNullOrWhiteSpace(keyPath))
    keyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "intake-gate.secret-key");
if (string.Equals(Path.GetFullPath(keyPath), databasePath, StringComparison.Ordinal))
    throw new ConfigurationValidationException("SecretStore:KeyPath must be separate from the operational database.");
var dataProtectionKeyPath = builder.Configuration["Authentication:DataProtectionKeyPath"];
if (string.IsNullOrWhiteSpace(dataProtectionKeyPath))
    dataProtectionKeyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "data-protection-keys");
dataProtectionKeyPath = Path.GetFullPath(dataProtectionKeyPath);
if (string.Equals(dataProtectionKeyPath, databasePath, StringComparison.Ordinal) ||
    string.Equals(dataProtectionKeyPath, Path.GetFullPath(keyPath), StringComparison.Ordinal))
{
    throw new ConfigurationValidationException(
        "Authentication:DataProtectionKeyPath must be separate from the database and credential encryption key.");
}
Directory.CreateDirectory(dataProtectionKeyPath);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(dataProtectionKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
    .SetApplicationName("EngineeringIntakeGate");
var secretStore = new SqliteSecretStore(databasePath, new InstallationKeyProvider(keyPath));
await secretStore.ValidateAsync();
var providerOverride = builder.Configuration["AiRuntime:Provider"];
var modelOverride = builder.Configuration["AiRuntime:Model"];
var credentialReferenceOverride = builder.Configuration["AiRuntime:CredentialEnvironmentVariable"];
var timeoutOverride = builder.Configuration.GetValue<int?>("AiRuntime:TimeoutSeconds");
var aiRuntimeOverrideRequested = !string.IsNullOrWhiteSpace(providerOverride) ||
                                 !string.IsNullOrWhiteSpace(modelOverride) ||
                                 !string.IsNullOrWhiteSpace(credentialReferenceOverride) ||
                                 timeoutOverride is not null;
if (aiRuntimeOverrideRequested &&
    !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test"))
{
    throw new ConfigurationValidationException(
        "AiRuntime profile overrides are restricted to explicit Development/Test smoke harnesses; Product runtime authority is SQLite.");
}
if (deploymentConfiguration is not null &&
    !string.IsNullOrWhiteSpace(providerOverride) &&
    !string.Equals(providerOverride, "openai", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(providerOverride, "anthropic", StringComparison.OrdinalIgnoreCase))
    throw new ConfigurationValidationException("AiRuntime:Provider must be openai or anthropic when supplied.");
if (deploymentConfiguration is not null && timeoutOverride is <= 0)
    throw new ConfigurationValidationException("AiRuntime:TimeoutSeconds must be greater than zero when supplied.");
if (deploymentConfiguration is not null && aiRuntimeOverrideRequested)
{
    deploymentConfiguration = deploymentConfiguration with
    {
        Profile = deploymentConfiguration.Profile with
        {
            Ai = deploymentConfiguration.Profile.Ai with
            {
                Provider = providerOverride?.Trim().ToLowerInvariant() ?? deploymentConfiguration.Profile.Ai.Provider,
                Model = modelOverride?.Trim() ?? deploymentConfiguration.Profile.Ai.Model,
                Authentication = string.IsNullOrWhiteSpace(credentialReferenceOverride)
                    ? deploymentConfiguration.Profile.Ai.Authentication
                    : new CredentialReference(credentialReferenceOverride.Trim()),
                TimeoutSeconds = timeoutOverride ?? deploymentConfiguration.Profile.Ai.TimeoutSeconds
            }
        }
    };
}
if (deploymentConfiguration is not null &&
    builder.Configuration.GetValue<bool>("AiRuntime:RequireDryRun") &&
    !deploymentConfiguration.Profile.Processing.DryRun)
    throw new ConfigurationValidationException("The real-AI smoke environment requires processing.executionMode=DRY_RUN.");

var adoOrganizationOverride = builder.Configuration["AdoRuntime:OrganizationUrl"];
var adoProjectOverride = builder.Configuration["AdoRuntime:Project"];
var adoQueryOverride = builder.Configuration["AdoRuntime:SavedQueryId"];
var adoCredentialOverride = builder.Configuration["AdoRuntime:CredentialEnvironmentVariable"];
var adoRuntimeOverrideRequested = !string.IsNullOrWhiteSpace(adoOrganizationOverride) ||
                                  !string.IsNullOrWhiteSpace(adoProjectOverride) ||
                                  !string.IsNullOrWhiteSpace(adoQueryOverride) ||
                                  !string.IsNullOrWhiteSpace(adoCredentialOverride);
if (adoRuntimeOverrideRequested &&
    !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test"))
{
    throw new ConfigurationValidationException(
        "AdoRuntime profile overrides are restricted to explicit Development/Test smoke harnesses; Product runtime authority is SQLite.");
}
if (deploymentConfiguration is not null && adoRuntimeOverrideRequested)
{
    if (!string.IsNullOrWhiteSpace(adoOrganizationOverride) &&
        (!Uri.TryCreate(adoOrganizationOverride, UriKind.Absolute, out var parsedOrganization) ||
         parsedOrganization.Scheme is not ("http" or "https")))
        throw new ConfigurationValidationException("AdoRuntime:OrganizationUrl must be an absolute HTTP(S) URL when supplied.");
    if (!string.IsNullOrWhiteSpace(adoQueryOverride) && !Guid.TryParse(adoQueryOverride, out _))
        throw new ConfigurationValidationException("AdoRuntime:SavedQueryId must be a UUID when supplied.");

    deploymentConfiguration = deploymentConfiguration with
    {
        Profile = deploymentConfiguration.Profile with
        {
            Ado = deploymentConfiguration.Profile.Ado with
            {
                OrganizationUrl = string.IsNullOrWhiteSpace(adoOrganizationOverride)
                    ? deploymentConfiguration.Profile.Ado.OrganizationUrl
                    : new Uri(adoOrganizationOverride.Trim()),
                Project = adoProjectOverride?.Trim() ?? deploymentConfiguration.Profile.Ado.Project,
                SavedQueryId = string.IsNullOrWhiteSpace(adoQueryOverride)
                    ? deploymentConfiguration.Profile.Ado.SavedQueryId
                    : Guid.Parse(adoQueryOverride),
                Authentication = string.IsNullOrWhiteSpace(adoCredentialOverride)
                    ? deploymentConfiguration.Profile.Ado.Authentication
                    : new CredentialReference(adoCredentialOverride.Trim())
            }
        }
    };
}

if (deploymentConfiguration is not null)
{
    deploymentConfiguration = new DeploymentConfigurationValidator().Validate(
        DeploymentConfigurationDocuments.Profile(deploymentConfiguration.Profile),
        DeploymentConfigurationDocuments.Policy(deploymentConfiguration.Policy),
        "SQLite profile with runtime overrides",
        "SQLite policy");
}

if (deploymentConfiguration is not null)
{
    await secretStore.ImportEnvironmentReferenceIfMissingAsync(
        CredentialSlot.AzureDevOps, deploymentConfiguration.Profile.Ado.Authentication.EnvironmentVariable);
    var aiSlot = deploymentConfiguration.Profile.Ai.Provider == "openai"
        ? CredentialSlot.OpenAi
        : CredentialSlot.Anthropic;
    await secretStore.ImportEnvironmentReferenceIfMissingAsync(
        aiSlot, deploymentConfiguration.Profile.Ai.Authentication.EnvironmentVariable);
}

var aiConfigurationRepository = new SqliteAiConfigurationRepository(
    databaseOptions.Path, deploymentConfigurationValidator);
var azureDevOpsConfigurationRepository = new SqliteAzureDevOpsConfigurationRepository(
    databaseOptions.Path, deploymentConfigurationValidator);
var runtimeGenerationRepository = new SqliteRuntimeConfigurationGenerationRepository(
    databaseOptions.Path, deploymentConfigurationValidator);
var activeGeneration = await runtimeGenerationRepository.GetActiveAsync();
var configurationState = new DeploymentConfigurationState(activeGeneration?.Configuration ?? deploymentConfiguration);
if (activeGeneration is not null)
{
    configurationState.Activate(activeGeneration);
    var activeAdoCredential = await secretStore.GetMetadataAsync(activeGeneration.AzureDevOpsCredential.Slot);
    var activeAiCredential = await secretStore.GetMetadataAsync(activeGeneration.AiCredential.Slot);
    if (deploymentConfiguration is null ||
        activeAdoCredential.UpdatedAtUtc != activeGeneration.AzureDevOpsCredential.Revision ||
        activeAdoCredential.SourceKind != activeGeneration.AzureDevOpsCredential.SourceKind ||
        activeAiCredential.UpdatedAtUtc != activeGeneration.AiCredential.Revision ||
        activeAiCredential.SourceKind != activeGeneration.AiCredential.SourceKind ||
        !string.Equals(activeGeneration.ConfigurationFingerprint,
            RuntimeConfigurationFingerprint.Create(deploymentConfiguration,
                activeGeneration.AzureDevOpsCredential.Revision, activeGeneration.AiCredential.Revision),
            StringComparison.Ordinal))
        configurationState.MarkActivationPending();
}
builder.Services.AddSingleton(configurationState);
if (deploymentConfiguration is not null) builder.Services.AddSingleton(deploymentConfiguration);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IDeploymentConfigurationValidator>(deploymentConfigurationValidator);
builder.Services.AddSingleton(databaseMigrator);
builder.Services.AddSingleton<ISingletonProfileRepository>(singletonProfileRepository);
builder.Services.AddSingleton<ISecretStore>(secretStore);
builder.Services.AddSingleton<ISetupProgressRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteSetupProgressRepository(options.Path);
});
builder.Services.AddSingleton<IOnboardingProfileDraftRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteOnboardingProfileDraftRepository(options.Path);
});
builder.Services.AddSingleton<IScheduleConfigurationValidator, CronScheduleConfigurationValidator>();
builder.Services.AddSingleton<ILocalUserRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteLocalUserRepository(options.Path);
});
builder.Services.AddSingleton<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();
builder.Services.AddScoped<LocalAuthenticationService>();
builder.Services.AddScoped<LocalCookieAuthenticationEvents>();
builder.Services.AddSingleton<IApplicationRuntimeRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteApplicationRuntimeRepository(options.Path);
});
builder.Services.AddSingleton<IAuditRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteAuditRepository(options.Path);
});
builder.Services.AddSingleton<IControlPlaneAuditRepository>(services =>
    (IControlPlaneAuditRepository)services.GetRequiredService<IAuditRepository>());
builder.Services.AddSingleton<IControlPlaneAuditWriter>(services =>
    (IControlPlaneAuditWriter)services.GetRequiredService<IAuditRepository>());
builder.Services.AddSingleton<IReconciliationRepository>(services =>
    (IReconciliationRepository)services.GetRequiredService<IAuditRepository>());
builder.Services.AddSingleton<IIncrementalDiscoveryRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteIncrementalDiscoveryRepository(options.Path);
});
builder.Services.AddSingleton<IOperationalAuditReader>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteOperationalAuditReader(options.Path);
});
builder.Services.AddSingleton<IHomeSummaryReader>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteHomeSummaryReader(options.Path);
});
builder.Services.AddSingleton<OperationalRunReadService>();
builder.Services.AddSingleton<SupportabilityReadService>();
builder.Services.AddSingleton<HomeSummaryReadService>();
builder.Services.AddSingleton<IAzureDevOpsConfigurationRepository>(azureDevOpsConfigurationRepository);
builder.Services.AddSingleton<IAzureDevOpsSetupRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteAzureDevOpsSetupRepository(options.Path);
});
builder.Services.AddSingleton<IAiConfigurationRepository>(aiConfigurationRepository);
var pricingFreshnessDays = builder.Configuration.GetValue("AiPricing:FreshnessDays", 7);
if (pricingFreshnessDays <= 0)
    throw new ConfigurationValidationException("AiPricing:FreshnessDays must be greater than zero.");
builder.Services.AddSingleton(new AiModelPricingOptions
{
    FreshnessTtl = TimeSpan.FromDays(pricingFreshnessDays)
});
builder.Services.AddSingleton<IAiModelPricingRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteAiModelPricingRepository(options.Path);
});
builder.Services.AddSingleton<IAiModelPricingSource, BundledAiModelPricingCatalog>();
builder.Services.AddSingleton<AiModelPricingService>();
builder.Services.AddSingleton<IRuntimeConfigurationGenerationRepository>(runtimeGenerationRepository);
builder.Services.AddSingleton<IRuntimeConfigurationActivator, RuntimeConfigurationActivator>();
builder.Services.AddSingleton<IProfileManagementRepository>(services =>
{
    var options = services.GetRequiredService<IOptions<OperationalDatabaseOptions>>().Value;
    return new SqliteProfileManagementRepository(options.Path,
        services.GetRequiredService<IDeploymentConfigurationValidator>());
});
builder.Services.AddSingleton<ProfileManagementService>();
builder.Services.AddSingleton<OnboardingSetupService>();
builder.Services.AddSingleton<RuntimeRegistrationService>();
builder.Services.AddSingleton<SetupStateService>();
builder.Services.AddHostedService<RuntimeRegistrationHostedService>();
builder.Services.AddSingleton<IContentNormalizer, HtmlContentNormalizer>();
builder.Services.AddSingleton<ISecretRedactor, SecretRedactor>();
builder.Services.AddSingleton<IEvidenceProcessingLog, StructuredEvidenceProcessingLog>();
builder.Services.AddSingleton<IAttachmentProcessor, TextAttachmentProcessor>();
builder.Services.AddSingleton<IAttachmentProcessor, PdfAttachmentProcessor>();
builder.Services.AddSingleton<IAttachmentProcessor, ImageAttachmentProcessor>();
builder.Services.AddSingleton<IAttachmentProcessingService, AttachmentProcessingService>();
builder.Services.AddSingleton<IEvidencePreprocessor, EvidencePreprocessor>();
builder.Services.AddSingleton<IEvaluationRequestBuilder, EvaluationRequestBuilder>();
builder.Services.AddSingleton<EvaluationResponseParser>();
builder.Services.AddSingleton<EvaluationContractValidator>();
builder.Services.AddSingleton<IEvaluationLog, StructuredEvaluationLog>();
builder.Services.AddSingleton<IProviderCredentialResolver, EnvironmentProviderCredentialResolver>();
var realAiEnabled = builder.Configuration.GetValue<bool>("AiRuntime:Enabled") ||
                    (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test"));
builder.Services.AddSingleton<IIntakeAiProvider>(services =>
{
    if (!realAiEnabled)
    {
        var fakeScenario = builder.Configuration["AiRuntime:FakeScenario"] ?? "pass";
        return string.Equals(fakeScenario, "pass", StringComparison.OrdinalIgnoreCase)
            ? FakeIntakeAiProvider.ReusablePass()
            : FakeIntakeAiProvider.ForScenario(fakeScenario);
    }
    var configuration = services.GetRequiredService<DeploymentConfiguration>();
    var ai = configuration.Profile.Ai;
    var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var resolver = services.GetRequiredService<IProviderCredentialResolver>();
    return ai.Provider switch
    {
        "openai" => new OpenAiIntakeAiProvider(client, resolver, ai.Authentication.EnvironmentVariable, TimeSpan.FromSeconds(ai.TimeoutSeconds)),
        "anthropic" => new AnthropicIntakeAiProvider(client, resolver, ai.Authentication.EnvironmentVariable, TimeSpan.FromSeconds(ai.TimeoutSeconds)),
        _ => throw new ConfigurationValidationException($"Unsupported AI provider '{ai.Provider}'.")
    };
});
builder.Services.AddSingleton<IIntakeEvaluationService, IntakeEvaluationService>();
builder.Services.AddSingleton<CostEstimator>();
builder.Services.AddSingleton<IAiCostAccountingLog, StructuredAiCostAccountingLog>();
builder.Services.AddSingleton<IAiCostAccountingService, AiCostAccountingService>();
builder.Services.AddSingleton<IIntakeCommentRenderer, IntakeCommentRenderer>();
builder.Services.AddSingleton<IIntakeDecisionHandler, IntakeDecisionHandler>();
builder.Services.AddSingleton<IRunAuditLog, StructuredRunAuditLog>();
builder.Services.AddSingleton<IWorkItemReadLog, StructuredWorkItemReadLog>();
builder.Services.AddSingleton<IAzureDevOpsManagementClientFactory, AzureDevOpsManagementClientFactory>();
builder.Services.AddSingleton<AzureDevOpsManagementService>();
var deterministicAiManagementEnabled = builder.Configuration.GetValue<bool>("AiManagement:UseDeterministicFake");
if (deterministicAiManagementEnabled &&
    !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test"))
    throw new ConfigurationValidationException(
        "The deterministic AI management client is restricted to Development and Test environments.");
if (deterministicAiManagementEnabled)
    builder.Services.AddSingleton<IAiManagementClientFactory, DeterministicAiManagementClientFactory>();
else
    builder.Services.AddSingleton<IAiManagementClientFactory, AiManagementClientFactory>();
builder.Services.AddSingleton<AiManagementService>();
builder.Services.AddSingleton<IAzureDevOpsCredentialResolver, EnvironmentAzureDevOpsCredentialResolver>();
builder.Services.AddSingleton<IWorkItemSource>(services =>
{
    var configuration = services.GetRequiredService<DeploymentConfiguration>();
    return new AzureDevOpsWorkItemSource(
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        configuration.Profile.Ado,
        services.GetRequiredService<IAzureDevOpsCredentialResolver>(),
        services.GetRequiredService<IWorkItemReadLog>(),
        configuration.Profile.Processing.Retries,
        configuration.Profile.Processing.AttachmentLimits.MaximumBytesPerAttachment,
        enableAttachmentDownloads: builder.Configuration.GetValue("AdoRuntime:ReadAttachments", true));
});
builder.Services.AddSingleton<IWorkItemWriter>(services =>
{
    var configuration = services.GetRequiredService<DeploymentConfiguration>();
    return new AzureDevOpsWorkItemWriter(
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        configuration.Profile.Ado,
        services.GetRequiredService<IAzureDevOpsCredentialResolver>());
});
builder.Services.AddSingleton<IRetryDelay, SystemRetryDelay>();
builder.Services.AddSingleton<MutationReconciliationService>();
builder.Services.AddSingleton<IWorkItemEligibilityEvaluator, WorkItemEligibilityEvaluator>();
builder.Services.AddSingleton<IntakeRunService>();
builder.Services.AddSingleton<ManualWorkItemRunService>();
builder.Services.AddSingleton<IncrementalRunService>();
var useRegisteredRuntimeAdapters = builder.Configuration.GetValue("Runtime:UseRegisteredAdapters",
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test"));
if (useRegisteredRuntimeAdapters)
    builder.Services.AddSingleton<IRuntimeAzureDevOpsAdapterFactory, RegisteredRuntimeAzureDevOpsAdapterFactory>();
else
    builder.Services.AddSingleton<IRuntimeAzureDevOpsAdapterFactory, AzureDevOpsRuntimeAdapterFactory>();
if (realAiEnabled && !useRegisteredRuntimeAdapters)
    builder.Services.AddSingleton<IRuntimeAiProviderFactory, AiRuntimeProviderFactory>();
else
    builder.Services.AddSingleton<IRuntimeAiProviderFactory, RegisteredRuntimeAiProviderFactory>();
builder.Services.AddSingleton<IRuntimeExecutionFactory, RuntimeExecutionFactory>();
builder.Services.AddSingleton<IIncrementalScheduleCalculator, IncrementalScheduleCalculator>();
builder.Services.AddHostedService<IncrementalRunHostedService>();

var app = builder.Build();

if (activeGeneration is null && deploymentConfiguration is not null)
{
    var bootstrapActivation = await app.Services.GetRequiredService<IRuntimeConfigurationActivator>()
        .ActivateAuthoritativeAsync(
            new AuditActor(Guid.Parse("00000000-0000-0000-0000-000000000001"), "system"),
            ["startupRecovery"], app.Lifetime.ApplicationStopping);
    if (!bootstrapActivation.Succeeded)
    {
        app.Logger.LogWarning(
            "Persisted configuration has no usable active runtime generation. Event={EventName} Failure={Failure}",
            "RuntimeConfigurationActivationUnavailable", bootstrapActivation.Failure);
    }
}

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
            "InvalidRequest", "The request body is malformed or contains unsupported fields."));
    }
});
app.UseStatusCodePages(async context =>
{
    if (context.HttpContext.Response.StatusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden))
        return;
    var forbidden = context.HttpContext.Response.StatusCode == StatusCodes.Status403Forbidden;
    await context.HttpContext.Response.WriteAsJsonAsync(new ApiErrorResponse(
        forbidden ? "Forbidden" : "Unauthenticated",
        forbidden ? "The authenticated user is not authorized for this operation." : "Authentication is required."));
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapLocalAuthenticationEndpoints();
app.MapAzureDevOpsManagementEndpoints();
app.MapAiManagementEndpoints();
app.MapProfileManagementEndpoints();
app.MapOperationalEndpoints();
app.MapSupportabilityEndpoints();
app.MapHomeSummaryEndpoints();
app.MapOpenApi()
    .RequireAuthorization(LocalAuthPolicies.Admin)
    .WithDescription("Admin-only OpenAPI document. Authentication and endpoint authorization remain enforced when operations are invoked.");

app.MapGet("/health/live", () => TypedResults.Ok(new { status = "live" }));

app.MapGet(
    "/health/ready",
    async Task<Results<Ok<object>, JsonHttpResult<object>>> (
        IApplicationRuntimeRepository repository,
        SetupStateService setupState,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        if (await repository.CanAccessAsync(cancellationToken))
        {
            var setup = await setupState.GetAsync(cancellationToken);
            return TypedResults.Ok<object>(new
            {
                status = "ready",
                setupStatus = setup.SetupComplete ? "configured" : "incomplete"
            });
        }

        loggerFactory.CreateLogger("Readiness").LogWarning(
            "Operational persistence readiness check failed. Event={EventName}",
            "ReadinessFailed");
        return TypedResults.Json<object>(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    });

app.MapGet(
    "/api/version",
    (IHostEnvironment environment) => TypedResults.Ok(new VersionResponse(
        ProductMetadata.ApplicationName,
        ProductMetadata.Version,
        environment.EnvironmentName)))
    .RequireAuthorization(LocalAuthPolicies.Authenticated)
    .Produces<VersionResponse>()
    .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
    .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test"))
{
    app.MapPost(
        "/api/diagnostics/evidence/prepare",
        async Task<IResult> (
            RawWorkItem workItem,
            IEvidencePreprocessor preprocessor,
            DeploymentConfigurationState configurationState,
            CancellationToken cancellationToken) =>
        {
            if (configurationState.Configuration is not { } configuration) return ProfileRequiredUnavailable();
            return TypedResults.Ok(await preprocessor.PrepareAsync(workItem, configuration.Profile.Processing, cancellationToken));
        })
        .ExcludeFromDescription()
        .RequireAuthorization(LocalAuthPolicies.Admin)
        .RequireApiAntiforgery();

    app.MapPost(
        "/api/diagnostics/evaluations/{scenario}",
        async Task<IResult> (
            string scenario,
            RawWorkItem workItem,
            IRuntimeExecutionFactory runtimeFactory,
            IEvidencePreprocessor preprocessor,
            DeploymentConfigurationState configurationState,
            IEvaluationRequestBuilder requestBuilder,
            EvaluationResponseParser responseParser,
            EvaluationContractValidator contractValidator,
            IEvaluationLog evaluationLog,
            CancellationToken cancellationToken) =>
        {
            if (!configurationState.IsConfigured) return ProfileRequiredUnavailable();
            var capture = await runtimeFactory.CaptureAsync(cancellationToken);
            if (!capture.Succeeded) return ConfigurationUnavailable(capture.Failure);
            var configuration = capture.Services!.Generation.Configuration;
            var configuredProvider = capture.Services.AiProvider;
            var evidence = await preprocessor.PrepareAsync(workItem, configuration.Profile.Processing, cancellationToken);
            var service = new IntakeEvaluationService(
                requestBuilder,
                string.Equals(scenario, "real", StringComparison.OrdinalIgnoreCase) ? configuredProvider : FakeIntakeAiProvider.ForScenario(scenario),
                responseParser,
                contractValidator,
                evaluationLog);
            return TypedResults.Ok(await service.EvaluateAsync(evidence, configuration, cancellationToken));
        })
        .ExcludeFromDescription()
        .RequireAuthorization(LocalAuthPolicies.Admin)
        .RequireApiAntiforgery();

    app.MapPost(
        "/api/diagnostics/runs/{scenario}",
        async Task<IResult> (
            string scenario,
            RawWorkItem workItem,
            IRuntimeExecutionFactory runtimeFactory,
            IEvidencePreprocessor preprocessor,
            DeploymentConfigurationState configurationState,
            IEvaluationRequestBuilder requestBuilder,
            EvaluationResponseParser responseParser,
            EvaluationContractValidator contractValidator,
            IEvaluationLog evaluationLog,
            IIntakeDecisionHandler decisionHandler,
            IAuditRepository auditRepository,
            IClock clock,
            IRunAuditLog runAuditLog,
            IAiCostAccountingService costAccounting,
            CancellationToken cancellationToken) =>
        {
            if (!configurationState.IsConfigured) return ProfileRequiredUnavailable();
            var capture = await runtimeFactory.CaptureAsync(cancellationToken);
            if (!capture.Succeeded) return ConfigurationUnavailable(capture.Failure);
            var configuration = capture.Services!.Generation.Configuration;
            var configuredProvider = capture.Services.AiProvider;
            var evaluationService = new IntakeEvaluationService(
                requestBuilder,
                string.Equals(scenario, "real", StringComparison.OrdinalIgnoreCase) ? configuredProvider : FakeIntakeAiProvider.ForScenario(scenario),
                responseParser,
                contractValidator,
                evaluationLog);
            var runService = new IntakeRunService(
                preprocessor, evaluationService, decisionHandler, auditRepository, clock, runAuditLog, costAccounting);
            try
            {
                return TypedResults.Ok(await runService.ExecuteAsync(
                    workItem, configuration, RunTriggerType.ManualWorkItem,
                    $"diagnostic:{scenario.ToLowerInvariant()}", cancellationToken));
            }
            catch (LiveExecutionUnavailableException exception)
            {
                return TypedResults.Conflict(new { error = "LiveExecutionUnavailable", message = exception.Message });
            }
        })
        .ExcludeFromDescription()
        .RequireAuthorization(LocalAuthPolicies.Admin)
        .RequireApiAntiforgery();

    app.MapGet(
        "/api/diagnostics/runs/{runId:guid}",
        async Task<IResult> (Guid runId, IAuditRepository repository, CancellationToken cancellationToken) =>
        {
            var run = await repository.GetRunAsync(runId, cancellationToken);
            if (run is null) return TypedResults.NotFound();
            var evaluation = await repository.GetEvaluationForRunAsync(runId, cancellationToken);
            return TypedResults.Ok(new { run, evaluation });
        })
        .RequireAuthorization(LocalAuthPolicies.Authenticated);

    app.MapGet(
        "/api/diagnostics/runtime-records",
        async (IApplicationRuntimeRepository repository, CancellationToken cancellationToken) =>
        {
            var records = await repository.ListAsync(cancellationToken);
            return TypedResults.Ok(new
            {
                count = records.Count,
                records = records.Select(record => new
                {
                    id = record.Id,
                    instanceId = record.InstanceId,
                    startedAtUtc = record.StartedAtUtc,
                    applicationVersion = record.ApplicationVersion
                })
            });
        })
        .RequireAuthorization(LocalAuthPolicies.Authenticated);
}

await app.RunAsync();
return 0;

static string? ReadLegacyImportPath(string[] arguments)
{
    var indexes = arguments.Select((value, index) => (value, index))
        .Where(item => string.Equals(item.value, "--import-legacy-profile", StringComparison.Ordinal))
        .Select(item => item.index)
        .ToArray();
    if (indexes.Length == 0) return null;
    if (indexes.Length != 1 || indexes[0] + 1 >= arguments.Length ||
        string.IsNullOrWhiteSpace(arguments[indexes[0] + 1]) ||
        arguments[indexes[0] + 1].StartsWith("--", StringComparison.Ordinal))
    {
        throw new ArgumentException("Use --import-legacy-profile <exact-profile-yaml-path> exactly once.");
    }

    return arguments[indexes[0] + 1];
}

static IResult ProfileRequiredUnavailable() => Results.Json(
    new
    {
        error = "ProfileNotConfigured",
        message = "No deployment profile is configured. Explicitly import a legacy profile before running this operation."
    },
    statusCode: StatusCodes.Status503ServiceUnavailable);

static IResult ConfigurationUnavailable(RuntimeExecutionCaptureFailure? failure) => Results.Json(new
{
    error = failure == RuntimeExecutionCaptureFailure.CredentialRevisionUnavailable
            ? "CredentialRevisionUnavailable"
            : "NoActiveConfigurationGeneration",
    message = failure == RuntimeExecutionCaptureFailure.CredentialRevisionUnavailable
            ? "The active generation's exact credential revision is no longer available; validate and activate the replacement for a future run."
            : "No validated runtime configuration generation is active."
}, statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;

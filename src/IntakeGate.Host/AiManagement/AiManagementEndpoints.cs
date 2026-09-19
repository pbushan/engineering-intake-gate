using System.Security.Claims;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.AiPricing;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Host.Authentication;

namespace IntakeGate.Host.AiManagement;

public static class AiManagementEndpoints
{
    public static void MapAiManagementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai");

        MapProvider(group, AiProviderNames.OpenAi);
        MapProvider(group, AiProviderNames.Anthropic);

        group.MapGet("/settings", async (
                IAiConfigurationRepository configurations,
                ISecretStore secrets,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                var settings = await configurations.GetAsync(cancellationToken);
                CredentialMetadata? credential = settings.Provider is null
                    ? null
                    : await secrets.GetMetadataAsync(AiProviderNames.CredentialSlot(settings.Provider), cancellationToken);
                var modelReady = settings.ModelConfirmed && credential is not null &&
                                 credential.VerificationStatus == CredentialVerificationStatus.Verified &&
                                 settings.CredentialUpdatedAtUtc == credential.UpdatedAtUtc;
                return Results.Ok(new AiSettingsResponse(
                    settings.ProfileConfigured, settings.Provider, settings.Model,
                    settings.ModelConfirmed, modelReady, settings.ModelValidatedAtUtc,
                    false,
                    runtime.RuntimeActivationCurrent
                        ? "The confirmed provider and model are active for future executions."
                        : "The confirmed AI settings are not part of an active runtime generation."));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<AiSettingsResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPost("/model-candidates/validate", async (
                ValidateAiModelRequest request,
                ClaimsPrincipal principal,
                AiManagementService management,
                CancellationToken cancellationToken) =>
            {
                var result = await management.ValidateCandidateAsync(request.Provider, request.Model,
                    CurrentActor(principal), cancellationToken);
                return result.Candidate is { } candidate
                    ? Results.Ok(new ValidateAiModelResponse(candidate.ConfirmationToken,
                        candidate.ExpiresAtUtc, candidate.Provider, candidate.Model, candidate.DisplayName))
                    : CandidateFailure(result);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ValidateAiModelResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/model-candidates/confirm", async (
                ConfirmAiModelRequest request,
                ClaimsPrincipal principal,
                AiManagementService management,
                CancellationToken cancellationToken) =>
            {
                var result = await management.ConfirmAsync(request.ConfirmationToken,
                    CurrentActor(principal), cancellationToken);
                return result.Succeeded
                    ? Results.Ok(new ConfirmAiModelResponse(result.ProfileConfigured,
                        result.RestartRequired,
                        result.ProfileConfigured
                            ? "Validated AI configuration is active for future executions."
                            : "Validated AI configuration staged for singleton profile creation."))
                    : ConfirmationFailure(result.Failure);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ConfirmAiModelResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/pricing", async (
                IAiConfigurationRepository configurations,
                AiModelPricingService pricing,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            PricingResult(await ResolveConfirmedPricingAsync(
                configurations, pricing, loggerFactory, forceRefresh: false, cancellationToken)))
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<AiModelPricingResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPost("/pricing/refresh", async (
                IAiConfigurationRepository configurations,
                AiModelPricingService pricing,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            PricingResult(await ResolveConfirmedPricingAsync(
                configurations, pricing, loggerFactory, forceRefresh: true, cancellationToken)))
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<AiModelPricingResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);
    }

    private static async Task<(AiConfigurationSettings? Settings, AiModelPricingLookupResult? Result)>
        ResolveConfirmedPricingAsync(
            IAiConfigurationRepository configurations,
            AiModelPricingService pricing,
            ILoggerFactory loggerFactory,
            bool forceRefresh,
            CancellationToken cancellationToken)
    {
        var settings = await configurations.GetAsync(cancellationToken);
        if (!settings.ModelConfirmed || settings.Provider is null || settings.Model is null)
            return (null, null);
        var result = await pricing.GetAsync(
            settings.Provider, settings.Model, forceRefresh, cancellationToken);
        var logger = loggerFactory.CreateLogger("AiModelPricing");
        if (result.Refreshed)
        {
            logger.LogInformation(
                "AI model pricing refreshed. Event={EventName} Provider={Provider} Model={Model}",
                "AiModelPricingRefreshed", settings.Provider, settings.Model);
        }
        if (result.RefreshFailed)
        {
            logger.LogWarning(
                "AI model pricing refresh was unavailable. Event={EventName} Provider={Provider} Model={Model} Preserved={Preserved}",
                "AiModelPricingRefreshUnavailable", settings.Provider, settings.Model, result.Available);
        }
        if (result.Stale && result.Available)
        {
            logger.LogWarning(
                "Stale cached AI model pricing is in use. Event={EventName} Provider={Provider} Model={Model}",
                "AiModelPricingStaleCacheUsed", settings.Provider, settings.Model);
        }
        return (settings, result);
    }

    private static IResult PricingResult(
        (AiConfigurationSettings? Settings, AiModelPricingLookupResult? Result) resolution)
    {
        if (resolution is not ({ Provider: { } provider, Model: { } model }, { } result))
        {
            return Results.Conflict(new ApiErrorResponse(
                "AiModelNotConfirmed", "Validate and confirm an AI model before loading pricing."));
        }
        var value = result.Pricing;
        return Results.Ok(new AiModelPricingResponse(
            provider,
            model,
            result.Available,
            result.Stale,
            result.RefreshFailed,
            value?.InputPerMillionTokens,
            value?.CachedInputPerMillionTokens,
            value?.OutputPerMillionTokens,
            value?.Currency,
            value?.Source,
            value?.SourceUri,
            value?.CatalogVersion,
            value?.EffectiveAtUtc,
            value?.VerifiedAtUtc,
            value?.ExpiresAtUtc,
            value?.SourceKind switch
            {
                AiModelPricingSourceKind.BundledCatalog => "bundledCatalog",
                AiModelPricingSourceKind.AuthoritativeCatalog => "authoritativeCatalog",
                AiModelPricingSourceKind.ManualOverride => "manualOverride",
                _ => null
            }));
    }

    private static void MapProvider(RouteGroupBuilder group, string provider)
    {
        var providerGroup = group.MapGroup($"/providers/{provider}");

        providerGroup.MapGet("/credential", async (
                ISecretStore secrets, CancellationToken cancellationToken) =>
            Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(
                AiProviderNames.CredentialSlot(provider), cancellationToken))))
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        providerGroup.MapPut("/credential/local", async (
                AiCredentialReplacementRequest request,
                ClaimsPrincipal principal,
                AiManagementService management,
                ISecretStore secrets,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Replacement) || request.Replacement.Length > 4_096)
                    return Results.BadRequest(new ApiErrorResponse(
                        "ValidationFailed", "The AI credential needs attention.",
                        FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["ai.credential"] = ["Enter a credential between 1 and 4,096 characters."]
                        }));
                await management.ReplaceLocalCredentialAsync(provider, new SecretValue(request.Replacement),
                    CurrentActor(principal), cancellationToken);
                return Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(
                    AiProviderNames.CredentialSlot(provider), cancellationToken)));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        providerGroup.MapPut("/credential/environment", async (
                EnvironmentAiCredentialRequest request,
                ClaimsPrincipal principal,
                AiManagementService management,
                ISecretStore secrets,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await management.ReplaceEnvironmentCredentialAsync(provider,
                        request.EnvironmentVariableName ?? string.Empty, CurrentActor(principal), cancellationToken);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new ApiErrorResponse(
                        "ValidationFailed", "The AI environment reference needs attention.",
                        FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["ai.environmentVariableName"] = ["Enter a valid environment-variable name, not a credential value."]
                        }));
                }
                return Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(
                    AiProviderNames.CredentialSlot(provider), cancellationToken)));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest);

        providerGroup.MapPost("/credential-tests", async (
                ClaimsPrincipal principal,
                AiManagementService management,
                CancellationToken cancellationToken) =>
            VerificationResult(await management.VerifyCredentialAsync(provider,
                CurrentActor(principal), cancellationToken)))
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<AiCredentialTestResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        providerGroup.MapPost("/models/discover", async (
                ClaimsPrincipal principal,
                AiManagementService management,
                CancellationToken cancellationToken) =>
            DiscoveryResult(await management.DiscoverModelsAsync(provider,
                CurrentActor(principal), cancellationToken)))
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<AiModelDiscoveryResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult VerificationResult(AiCredentialVerificationResult result) => result.Succeeded
        ? Results.Ok(new AiCredentialTestResponse(true, null))
        : ManagementFailure(result.Failure, "AiCredentialVerificationFailed");

    private static IResult DiscoveryResult(AiModelDiscoveryResult result) => result.Succeeded
        ? Results.Ok(new AiModelDiscoveryResponse(true, result.Models, null))
        : ManagementFailure(result.Failure, "AiModelDiscoveryUnavailable");

    private static IResult CandidateFailure(AiCandidateResult result) => result.Failure switch
    {
        AiCandidateFailure.InvalidProvider => Results.BadRequest(new ApiErrorResponse(
            "ValidationFailed", "Select a supported AI provider.",
            FieldErrors: new Dictionary<string, IReadOnlyList<string>> { ["ai.provider"] = ["Select OpenAI or Anthropic."] })),
        AiCandidateFailure.InvalidModelIdentifier => Results.BadRequest(new ApiErrorResponse(
            "ValidationFailed", "The model ID needs attention.",
            FieldErrors: new Dictionary<string, IReadOnlyList<string>> { ["ai.model"] = ["Enter a valid model ID."] })),
        AiCandidateFailure.CredentialUnavailable => Results.Conflict(new ApiErrorResponse(
            "AiCredentialUnavailable", "Configure an AI credential before validating a model.")),
        AiCandidateFailure.CredentialNotVerified => Results.Conflict(new ApiErrorResponse(
            "AiCredentialNotVerified", "Verify the AI credential before validating a model.")),
        AiCandidateFailure.ProviderFailure => ManagementFailure(result.ProviderFailure, "AiModelValidationFailed"),
        _ => Results.Conflict(new { error = "AiModelCandidateConflict" })
    };

    private static IResult ConfirmationFailure(AiCandidateFailure? failure) => failure switch
    {
        AiCandidateFailure.CandidateStale => Results.Conflict(new ApiErrorResponse(
            "AiModelCandidateStale", "Model validation expired or the credential changed. Validate the model again.")),
        AiCandidateFailure.ActivationFailed => Results.Json(new { error = "ConfigurationActivationFailed" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict(new { error = "AiModelConfirmationConflict" })
    };

    private static IResult ManagementFailure(AiManagementFailure? failure, string fallback) => failure switch
    {
        AiManagementFailure.CredentialUnavailable => Results.Conflict(new ApiErrorResponse(
            "AiCredentialUnavailable", "Configure an AI credential before continuing.")),
        AiManagementFailure.CredentialNotVerified => Results.Conflict(new ApiErrorResponse(
            "AiCredentialNotVerified", "Verify the AI credential before continuing.")),
        AiManagementFailure.CredentialChanged => Results.Conflict(new ApiErrorResponse(
            "AiCredentialChanged", "The AI credential changed. Verify it again before continuing.")),
        AiManagementFailure.AuthenticationFailed => Results.Json(new ApiErrorResponse(
                "AiAuthenticationFailed", "The AI provider rejected the credential. Replace or correct it, then verify again.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ai.credential"] = ["The AI provider rejected the stored credential. Replace or correct it, then verify again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AiManagementFailure.AuthorizationFailed => Results.Json(new ApiErrorResponse(
                "AiAuthorizationFailed", "The AI provider denied access. Review credential permissions and model access, then verify again.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ai.credential"] = ["Review the credential permissions and account access, then verify again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AiManagementFailure.RateLimited => Results.Json(new ApiErrorResponse(
            "AiRateLimited", "The AI provider rate limit was reached. Wait and try again."),
            statusCode: StatusCodes.Status429TooManyRequests),
        AiManagementFailure.Timeout => Results.Json(new ApiErrorResponse(
            "AiProviderTimeout", "The AI provider timed out. Try again later; the credential was not marked invalid."),
            statusCode: StatusCodes.Status504GatewayTimeout),
        AiManagementFailure.ProviderUnavailable => Results.Json(new ApiErrorResponse(
            "AiProviderUnavailable", "The AI provider is temporarily unavailable. Try again later; the credential was not marked invalid."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        AiManagementFailure.ModelNotFound => Results.Json(new ApiErrorResponse(
                "ValidationFailed", "The selected model was not found or is not available to this account.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ai.model"] = ["Choose an available model or enter another model ID, then validate again."]
                }),
            statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Json(new ApiErrorResponse(
            fallback, "The AI operation could not be completed. Review system health and try again."),
            statusCode: StatusCodes.Status502BadGateway)
    };

    private static CredentialMetadataResponse SafeMetadata(CredentialMetadata metadata) => new(
        metadata.Configured, metadata.SourceKind, metadata.CreatedAtUtc, metadata.UpdatedAtUtc,
        metadata.VerificationStatus, metadata.LastVerifiedAtUtc, metadata.VerificationDiagnostic);

    private static AuditActor CurrentActor(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            string.IsNullOrWhiteSpace(principal.Identity?.Name))
            throw new InvalidOperationException("Authenticated user identity is incomplete.");
        return new AuditActor(id, principal.Identity.Name);
    }
}

public sealed record AiCredentialReplacementRequest(string? Replacement);
public sealed record EnvironmentAiCredentialRequest(string? EnvironmentVariableName);
public sealed record ValidateAiModelRequest(string? Provider, string? Model);
public sealed record ConfirmAiModelRequest(string? ConfirmationToken);
public sealed record AiCredentialTestResponse(bool Succeeded, string? Error);
public sealed record AiModelDiscoveryResponse(bool Succeeded, IReadOnlyList<AiModelDescriptor> Models, string? Error);
public sealed record ValidateAiModelResponse(
    string ConfirmationToken, DateTimeOffset ExpiresAtUtc, string Provider, string Model, string? DisplayName);
public sealed record ConfirmAiModelResponse(bool ProfileConfigured, bool RestartRequired, string ActivationMessage);
public sealed record AiSettingsResponse(
    bool ProfileConfigured,
    string? Provider,
    string? Model,
    bool ModelConfirmed,
    bool Ready,
    DateTimeOffset? ModelValidatedAtUtc,
    bool RestartRequired,
    string ActivationMessage);
public sealed record AiModelPricingResponse(
    string Provider,
    string Model,
    bool Available,
    bool Stale,
    bool RefreshFailed,
    decimal? InputPerMillionTokens,
    decimal? CachedInputPerMillionTokens,
    decimal? OutputPerMillionTokens,
    string? Currency,
    string? Source,
    Uri? SourceUri,
    string? CatalogVersion,
    DateTimeOffset? EffectiveAtUtc,
    DateTimeOffset? LastVerifiedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? SourceKind);

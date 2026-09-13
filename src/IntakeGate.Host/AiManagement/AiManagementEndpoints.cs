using System.Security.Claims;
using IntakeGate.Application.AiManagement;
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
                    return Results.BadRequest(new { error = "InvalidCredential" });
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
                    return Results.BadRequest(new { error = "InvalidEnvironmentReference" });
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
        AiCandidateFailure.InvalidProvider => Results.BadRequest(new { error = "InvalidAiProvider" }),
        AiCandidateFailure.InvalidModelIdentifier => Results.BadRequest(new { error = "InvalidModelIdentifier" }),
        AiCandidateFailure.CredentialUnavailable => Results.Conflict(new { error = "AiCredentialUnavailable" }),
        AiCandidateFailure.CredentialNotVerified => Results.Conflict(new { error = "AiCredentialNotVerified" }),
        AiCandidateFailure.ProviderFailure => ManagementFailure(result.ProviderFailure, "AiModelValidationFailed"),
        _ => Results.Conflict(new { error = "AiModelCandidateConflict" })
    };

    private static IResult ConfirmationFailure(AiCandidateFailure? failure) => failure switch
    {
        AiCandidateFailure.CandidateStale => Results.Conflict(new { error = "AiModelCandidateStale" }),
        AiCandidateFailure.ActivationFailed => Results.Json(new { error = "ConfigurationActivationFailed" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict(new { error = "AiModelConfirmationConflict" })
    };

    private static IResult ManagementFailure(AiManagementFailure? failure, string fallback) => failure switch
    {
        AiManagementFailure.CredentialUnavailable => Results.Conflict(new { error = "AiCredentialUnavailable" }),
        AiManagementFailure.CredentialNotVerified => Results.Conflict(new { error = "AiCredentialNotVerified" }),
        AiManagementFailure.CredentialChanged => Results.Conflict(new { error = "AiCredentialChanged" }),
        AiManagementFailure.AuthenticationFailed => Results.Json(new { error = "AiAuthenticationFailed" },
            statusCode: StatusCodes.Status502BadGateway),
        AiManagementFailure.AuthorizationFailed => Results.Json(new { error = "AiAuthorizationFailed" },
            statusCode: StatusCodes.Status502BadGateway),
        AiManagementFailure.RateLimited => Results.Json(new { error = "AiRateLimited" },
            statusCode: StatusCodes.Status429TooManyRequests),
        AiManagementFailure.Timeout => Results.Json(new { error = "AiProviderTimeout" },
            statusCode: StatusCodes.Status504GatewayTimeout),
        AiManagementFailure.ProviderUnavailable => Results.Json(new { error = "AiProviderUnavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        AiManagementFailure.ModelNotFound => Results.Json(new { error = "AiModelNotFound" },
            statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Json(new { error = fallback }, statusCode: StatusCodes.Status502BadGateway)
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

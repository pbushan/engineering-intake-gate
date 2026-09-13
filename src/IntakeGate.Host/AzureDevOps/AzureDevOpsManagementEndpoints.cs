using System.Security.Claims;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Host.Authentication;

namespace IntakeGate.Host.AzureDevOps;

public static class AzureDevOpsManagementEndpoints
{
    public static void MapAzureDevOpsManagementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ado");

        group.MapGet("/credential", async (ISecretStore secrets, CancellationToken cancellationToken) =>
            Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken))))
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPut("/credential/local", async (
                CredentialReplacementRequest request,
                ClaimsPrincipal principal,
                ISecretStore secrets,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Replacement) || request.Replacement.Length > 4_096)
                    return Results.BadRequest(new ApiErrorResponse(
                        "ValidationFailed", "The Azure DevOps credential needs attention.",
                        FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["ado.credential"] = ["Enter a credential between 1 and 4,096 characters."]
                        }));
                await secrets.ReplaceLocalAsync(CredentialSlot.AzureDevOps,
                    new SecretValue(request.Replacement), CurrentActor(principal), cancellationToken);
                runtime.MarkActivationPending();
                return Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(
                    CredentialSlot.AzureDevOps, cancellationToken)));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPut("/credential/environment", async (
                EnvironmentReferenceRequest request,
                ClaimsPrincipal principal,
                ISecretStore secrets,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await secrets.ConfigureEnvironmentReferenceAsync(CredentialSlot.AzureDevOps,
                        request.EnvironmentVariableName ?? string.Empty, CurrentActor(principal), cancellationToken);
                    runtime.MarkActivationPending();
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new ApiErrorResponse(
                        "ValidationFailed", "The environment reference needs attention.",
                        FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["ado.environmentVariableName"] = ["Enter a valid environment-variable name, not a credential value."]
                        }));
                }
                return Results.Ok(SafeMetadata(await secrets.GetMetadataAsync(
                    CredentialSlot.AzureDevOps, cancellationToken)));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<CredentialMetadataResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapGet("/settings", async (
                ISingletonProfileRepository profiles,
                IAzureDevOpsConfigurationRepository configurations,
                IAzureDevOpsSetupRepository setup,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                var configuration = await profiles.LoadAsync(cancellationToken);
                if (configuration is null)
                {
                    var staged = await setup.GetAsync(cancellationToken);
                    return Results.Ok(new AzureDevOpsSettingsResponse(false,
                        staged?.OrganizationUrl.AbsoluteUri, staged?.Project, staged?.SavedQueryId, 0,
                        staged?.ConfigurationFingerprint, staged?.SavedQueryId is not null,
                        staged?.QueryValidatedAtUtc, false));
                }
                var state = await configurations.GetStateAsync(cancellationToken);
                return Results.Ok(new AzureDevOpsSettingsResponse(
                    true,
                    configuration.Profile.Ado.OrganizationUrl.AbsoluteUri,
                    configuration.Profile.Ado.Project,
                    configuration.Profile.Ado.SavedQueryId,
                    state?.Generation ?? 0,
                    state?.Fingerprint ?? AzureDevOpsConfigurationFingerprint.Create(
                        configuration.Profile.Identity.Id, configuration.Profile.Ado),
                    state?.QueryConfirmed ?? false,
                    state?.QueryValidatedAtUtc,
                    false));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<AzureDevOpsSettingsResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", async (
                AzureDevOpsSetupSettingsRequest request,
                ClaimsPrincipal principal,
                AzureDevOpsManagementService management,
                CancellationToken cancellationToken) =>
            {
                var fieldErrors = new Dictionary<string, IReadOnlyList<string>>();
                if (!AzureDevOpsSettingsValidation.TryNormalizeOrganization(request.OrganizationUrl, out _))
                    fieldErrors["ado.organizationUrl"] = ["Enter an absolute Azure DevOps organization URL without user information, a query, or a fragment."];
                if (string.IsNullOrWhiteSpace(request.Project))
                    fieldErrors["ado.project"] = ["Project is required."];
                if (fieldErrors.Count > 0)
                    return Results.BadRequest(new ApiErrorResponse(
                        "ValidationFailed", "The Azure DevOps settings need attention.", FieldErrors: fieldErrors));
                var error = await management.SaveProfilelessSettingsAsync(request.OrganizationUrl,
                    request.Project, CurrentActor(principal), cancellationToken);
                return error switch
                {
                    null => Results.NoContent(),
                    "ValidatedCandidateRequired" => Results.Conflict(new
                    {
                        error,
                        message = "An existing profile's ADO settings can change only through saved-query validation and confirmation."
                    }),
                    _ => Results.BadRequest(new { error })
                };
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/connection-tests", async (
                ClaimsPrincipal principal,
                AzureDevOpsManagementService management,
                CancellationToken cancellationToken) =>
            {
                var result = await management.TestConnectionAsync(CurrentActor(principal), cancellationToken);
                return result.Succeeded
                    ? Results.Ok(new ConnectionTestResponse(true, null))
                    : Failure(result.Failure);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ConnectionTestResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/query-candidates/validate", async (
                ValidateSavedQueryRequest request,
                ClaimsPrincipal principal,
                AzureDevOpsManagementService management,
                CancellationToken cancellationToken) =>
            {
                var result = await management.ValidateCandidateAsync(request.OrganizationUrl,
                    request.Project, request.SavedQuery, CurrentActor(principal), cancellationToken);
                if (result.Candidate is { } candidate)
                    return Results.Ok(new ValidateSavedQueryResponse(
                        candidate.ConfirmationToken, candidate.ExpiresAtUtc, candidate.OrganizationUrl,
                        candidate.Project, candidate.SavedQueryId, candidate.CandidateFingerprint,
                        candidate.TotalCount, candidate.Preview));
                return CandidateFailure(result);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ValidateSavedQueryResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/query-candidates/confirm", async (
                ConfirmSavedQueryRequest request,
                ClaimsPrincipal principal,
                AzureDevOpsManagementService management,
                CancellationToken cancellationToken) =>
            {
                var result = await management.ConfirmAsync(request.ConfirmationToken,
                    CurrentActor(principal), cancellationToken);
                if (result.Succeeded)
                    return Results.Ok(new ConfirmSavedQueryResponse(
                        result.Generation!.Value, result.RestartRequired,
                        result.Generation == 0
                            ? "Validated query staged for singleton profile creation."
                            : "Validated ADO configuration is active for future executions."));
                return ConfirmationFailure(result.Failure);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ConfirmSavedQueryResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);
    }

    private static IResult Failure(AzureDevOpsManagementFailure? failure) => failure switch
    {
        AzureDevOpsManagementFailure.ProfileNotConfigured => ProfileRequired(),
        AzureDevOpsManagementFailure.CredentialUnavailable => Results.Conflict(new ApiErrorResponse(
            "AzureDevOpsCredentialUnavailable", "Configure an Azure DevOps credential before testing the connection.")),
        AzureDevOpsManagementFailure.AuthenticationFailed => Results.Json(new ApiErrorResponse(
                "AzureDevOpsAuthenticationFailed", "Azure DevOps rejected the credential. Replace or correct it, then test again.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ado.credential"] = ["Azure DevOps rejected the stored credential. Replace or correct it, then test again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.AuthorizationFailed => Results.Json(new ApiErrorResponse(
                "AzureDevOpsAuthorizationFailed", "Azure DevOps denied access. Review the credential permissions and project access, then test again.",
                SectionErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ado.connection"] = ["Review credential permissions and project access, then test the connection again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.OrganizationOrProjectUnavailable => Results.Json(new ApiErrorResponse(
                "AzureDevOpsOrganizationOrProjectUnavailable", "Azure DevOps could not find or access the saved organization and project. Review them, then test again.",
                SectionErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ado.connection"] = ["Review the organization URL, project, and access permissions, then test again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.Timeout => Results.Json(new ApiErrorResponse(
                "AzureDevOpsTimeout", "Azure DevOps timed out. Try again later; the credential was not marked invalid."),
            statusCode: StatusCodes.Status504GatewayTimeout),
        AzureDevOpsManagementFailure.ProviderUnavailable => Results.Json(new ApiErrorResponse(
                "AzureDevOpsProviderUnavailable", "Azure DevOps is temporarily unavailable. Try again later; the credential was not marked invalid."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new ApiErrorResponse(
                "AzureDevOpsConnectionFailed", "The Azure DevOps connection test could not be completed. Review the system health details and try again."),
            statusCode: StatusCodes.Status502BadGateway)
    };

    private static IResult CandidateFailure(AzureDevOpsCandidateResult result) => result.Failure switch
    {
        AzureDevOpsCandidateFailure.ProfileNotConfigured => ProfileRequired(),
        AzureDevOpsCandidateFailure.InvalidSettings => Results.BadRequest(new ApiErrorResponse(
            "ValidationFailed", "The Azure DevOps organization or project needs attention.",
            SectionErrors: new Dictionary<string, IReadOnlyList<string>>
            {
                ["ado.connection"] = ["Save a valid organization URL and project before validating the query."]
            })),
        AzureDevOpsCandidateFailure.InvalidSavedQueryInput => Results.BadRequest(new ApiErrorResponse(
            "ValidationFailed", "The saved query needs attention.",
            FieldErrors: new Dictionary<string, IReadOnlyList<string>>
            {
                ["ado.savedQuery"] = ["Enter an Azure DevOps saved-query URL or GUID."]
            })),
        AzureDevOpsCandidateFailure.CredentialUnavailable => Results.Conflict(new ApiErrorResponse(
            "AzureDevOpsCredentialNotConfigured", "Configure and verify an Azure DevOps credential before validating a saved query.")),
        AzureDevOpsCandidateFailure.ProviderFailure => QueryProviderFailure(result.ProviderFailure),
        _ => Results.Conflict(new { error = "SavedQueryCandidateConflict" })
    };

    private static IResult ConfirmationFailure(AzureDevOpsCandidateFailure? failure) => failure switch
    {
        AzureDevOpsCandidateFailure.ProfileNotConfigured => ProfileRequired(),
        AzureDevOpsCandidateFailure.CandidateStale => Results.Conflict(new ApiErrorResponse(
            "SavedQueryCandidateStale", "Validation expired or the connection changed. Validate the query again.")),
        AzureDevOpsCandidateFailure.ActiveRunInProgress => Results.Conflict(new { error = "ActiveRunInProgress" }),
        AzureDevOpsCandidateFailure.ActivationFailed => Results.Json(new { error = "ConfigurationActivationFailed" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict(new { error = "SavedQueryConfirmationConflict" })
    };

    private static IResult QueryProviderFailure(AzureDevOpsManagementFailure? failure) => failure switch
    {
        AzureDevOpsManagementFailure.QueryNotFoundOrInaccessible => Results.Json(new ApiErrorResponse(
                "SavedQueryNotFoundOrInaccessible", "The saved query could not be found or accessed.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ado.savedQuery"] = ["Check the saved-query URL or GUID and its permissions, then validate again."]
                }),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.AuthenticationFailed => Results.Json(new ApiErrorResponse(
                "AzureDevOpsAuthenticationFailed", "Azure DevOps rejected the credential. Return to the connection step, replace or correct it, then validate the query again."),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.AuthorizationFailed => Results.Json(new ApiErrorResponse(
                "AzureDevOpsAuthorizationFailed", "Azure DevOps denied access. Review credential permissions and project access, then validate the query again."),
            statusCode: StatusCodes.Status502BadGateway),
        AzureDevOpsManagementFailure.Timeout => Results.Json(new ApiErrorResponse(
                "AzureDevOpsTimeout", "Azure DevOps timed out. Try validating the query again later; the query input and credential were not marked invalid."),
            statusCode: StatusCodes.Status504GatewayTimeout),
        AzureDevOpsManagementFailure.ProviderUnavailable => Results.Json(new ApiErrorResponse(
                "AzureDevOpsProviderUnavailable", "Azure DevOps is temporarily unavailable. Try validating the query again later; the query input and credential were not marked invalid."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new ApiErrorResponse(
                "SavedQueryValidationFailed", "The saved query could not be validated. Review system health and try again."),
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

    private static IResult ProfileRequired() => Results.Json(new
    {
        error = "ProfileNotConfigured",
        message = "No deployment profile is configured. ADO profile settings require the singleton profile."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);
}

public sealed record CredentialReplacementRequest(string? Replacement);
public sealed record EnvironmentReferenceRequest(string? EnvironmentVariableName);
public sealed record AzureDevOpsSetupSettingsRequest(string? OrganizationUrl, string? Project);
public sealed record ValidateSavedQueryRequest(string? OrganizationUrl, string? Project, string? SavedQuery);
public sealed record ConfirmSavedQueryRequest(string? ConfirmationToken);
public sealed record ConnectionTestResponse(bool Succeeded, string? Error);
public sealed record AzureDevOpsSettingsResponse(
    bool ProfileConfigured,
    string? OrganizationUrl,
    string? Project,
    Guid? SavedQueryId,
    int ConfigurationGeneration,
    string? ConfigurationFingerprint,
    bool QueryConfirmed,
    DateTimeOffset? QueryValidatedAtUtc,
    bool RestartRequired);
public sealed record ValidateSavedQueryResponse(
    string ConfirmationToken,
    DateTimeOffset ExpiresAtUtc,
    string OrganizationUrl,
    string Project,
    Guid SavedQueryId,
    string CandidateFingerprint,
    int TotalCount,
    IReadOnlyList<AzureDevOpsQueryPreviewItem> Preview);
public sealed record ConfirmSavedQueryResponse(int ConfigurationGeneration, bool RestartRequired, string ActivationMessage);

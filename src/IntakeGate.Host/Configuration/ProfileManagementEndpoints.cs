using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Profiles;
using IntakeGate.Application.Setup;
using IntakeGate.Application.Time;
using IntakeGate.Host.Authentication;
using IntakeGate.Infrastructure.Configuration;

namespace IntakeGate.Host.Configuration;

public static class ProfileManagementEndpoints
{
    private const int MaximumLegacyDocumentCharacters = 262_144;
    private const int MaximumPortableDocumentCharacters = 524_288;
    private static readonly JsonSerializerOptions PortableJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapProfileManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/profile", async (
                ProfileManagementService profiles,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                var snapshot = await profiles.GetAsync(cancellationToken);
                return Results.Ok(ToState(snapshot, runtime));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .WithSummary("Read the safe singleton profile state")
            .WithDescription("Admin and Viewer. Returns exists=false for a profileless deployment; never returns credential material.")
            .Produces<ProfileStateResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/profile", async (
                ProfileWriteRequest request,
                ClaimsPrincipal principal,
                ProfileManagementService profiles,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                ProfileEditableConfiguration editable;
                try { editable = ToEditable(request); }
                catch (ProfileRequestValidationException) { return InvalidConfiguration(); }
                var result = await profiles.CreateAsync(editable, CurrentActor(principal), cancellationToken);
                return MutationResult(result, runtime, created: true);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Create the singleton profile from confirmed setup")
            .WithDescription("Admin and antiforgery token required. ADO and AI values come only from confirmed server-side staging; a second profile conflicts.")
            .Accepts<ProfileWriteRequest>("application/json")
            .Produces<ProfileStateResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPut("/api/profile", async (
                ProfileUpdateRequest request,
                ClaimsPrincipal principal,
                ProfileManagementService profiles,
                OnboardingSetupService onboarding,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                if (request.Profile is null)
                    return InvalidDraft();
                var validation = onboarding.ValidateDraft(request.Profile);
                if (!validation.IsEmpty || !onboarding.TryBuildEditable(request.Profile, out var editable) || editable is null)
                    return InvalidDraft(validation);
                var result = await profiles.UpdateAsync(request.ExpectedRevision, editable,
                    CurrentActor(principal), cancellationToken);
                return MutationResult(result, runtime, created: false);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Atomically update supported singleton profile fields")
            .WithDescription("Admin and antiforgery token required. The revision is optimistic concurrency control. Profile ID remains immutable; validated ADO query and AI changes are reconciled through their management workflows.")
            .Accepts<ProfileUpdateRequest>("application/json")
            .Produces<ProfileStateResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapPost("/api/profile/imports/legacy", async (
                LegacyProfileImportRequest request,
                ClaimsPrincipal principal,
                ProfileManagementService profiles,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.ProfileYaml) || string.IsNullOrWhiteSpace(request.PolicyYaml) ||
                    request.ProfileYaml.Length > MaximumLegacyDocumentCharacters ||
                    request.PolicyYaml.Length > MaximumLegacyDocumentCharacters)
                    return InvalidImport();
                DeploymentConfiguration configuration;
                try { configuration = new YamlDeploymentConfigurationLoader().LoadContents(request.ProfileYaml, request.PolicyYaml); }
                catch (ConfigurationValidationException) { return InvalidImport(); }
                var result = await profiles.ImportAsync(configuration, CurrentActor(principal), cancellationToken);
                return MutationResult(result, runtime, created: true);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Explicitly import one legacy YAML profile and policy")
            .WithDescription("Admin and antiforgery token required. Accepts bounded content, never a server path. Import is create-only, preserves profile_id, and rejects Production/LIVE.")
            .Accepts<LegacyProfileImportRequest>("application/json")
            .Produces<ProfileStateResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapGet("/api/profile/export", async (
                ProfileManagementService profiles,
                OnboardingSetupService onboarding,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                var draft = await onboarding.GetDraftAsync(cancellationToken);
                var snapshot = await profiles.GetAsync(cancellationToken);
                var exportedAt = clock.UtcNow.ToUniversalTime();
                if (draft is not null) return Results.Ok(ProfilePortability.Export(draft.Values, exportedAt));
                if (snapshot is not null) return Results.Ok(ProfilePortability.Export(snapshot.Configuration, exportedAt));
                return Results.Ok(ProfilePortability.Export(onboarding.GetDefaults().Values, exportedAt));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .WithSummary("Export the portable, secret-free Profile and Policy configuration")
            .WithDescription("Exports the current draft when present, otherwise the authoritative profile. Credentials, integration bindings, runtime state, history, and generated identifiers are excluded.")
            .Produces<PortableProfileDocument>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/profile/import/validate", async (
                HttpRequest request,
                OnboardingSetupService onboarding,
                CancellationToken cancellationToken) =>
            {
                var parsed = await ReadPortableDocumentAsync(request, cancellationToken);
                if (parsed.Error is not null) return parsed.Error;
                var values = ProfilePortability.ToDraft(parsed.Document!);
                if (!onboarding.IsPortableStructureSupported(values)) return InvalidPortableValues();
                var validation = onboarding.ValidateDraft(values);
                return Results.Ok(new ProfileImportPreviewResponse(parsed.Document!, validation.IsEmpty,
                    validation.FieldErrors, validation.SectionErrors));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Validate a portable Profile and Policy document without changing state")
            .Accepts<PortableProfileDocument>("application/json")
            .Produces<ProfileImportPreviewResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/profile/import", async (
                HttpRequest request,
                string? expectedProfileRevision,
                int? expectedDraftRevision,
                ClaimsPrincipal principal,
                ProfileManagementService profiles,
                OnboardingSetupService onboarding,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                var parsed = await ReadPortableDocumentAsync(request, cancellationToken);
                if (parsed.Error is not null) return parsed.Error;
                var values = ProfilePortability.ToDraft(parsed.Document!);
                if (!onboarding.IsPortableStructureSupported(values)) return InvalidPortableValues();
                var validation = onboarding.ValidateDraft(values);
                var current = await profiles.GetAsync(cancellationToken);
                if (current is not null && validation.IsEmpty &&
                    onboarding.TryBuildEditable(values, out var editable) && editable is not null)
                {
                    var result = await profiles.UpdateAsync(expectedProfileRevision, editable,
                        CurrentActor(principal), cancellationToken);
                    return result.Status == ProfileManagementStatus.Succeeded
                        ? Results.Ok(new ProfileImportResponse(true, ToState(result.Snapshot, runtime), null))
                        : MutationResult(result, runtime, created: false);
                }
                var draftResult = await onboarding.ImportDraftAsync(expectedDraftRevision, values,
                    CurrentActor(principal), cancellationToken);
                return draftResult.Status switch
                {
                    OnboardingDraftPersistenceStatus.Succeeded => Results.Ok(new ProfileImportResponse(false, null,
                        ToDraftState(draftResult.Draft, onboarding))),
                    OnboardingDraftPersistenceStatus.InvalidConfiguration => InvalidPortableValues(),
                    _ => Results.Conflict(new ApiErrorResponse("ProfileImportConflict",
                        "The Profile & Policy configuration changed after validation. Reload it and try again."))
                };
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Atomically replace the portable Profile and Policy configuration")
            .WithDescription("Complete configured profiles replace the authoritative profile. Incomplete imports replace the persisted draft and retain normal validation errors.")
            .Accepts<PortableProfileDocument>("application/json")
            .Produces<ProfileImportResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapGet("/api/setup/status", async (
                SetupStateService setup,
                CancellationToken cancellationToken) =>
            {
                var state = await setup.GetAsync(cancellationToken);
                return Results.Ok(new SetupStatusResponse(
                    state.InfrastructureReady,
                    state.AdminConfigured,
                    state.ProfileConfigured,
                    state.OnboardingDraftExists,
                    state.ProfileDetailsComplete,
                    state.PolicyDetailsComplete,
                    state.AzureDevOpsCredentialConfigured,
                    state.AzureDevOpsCredentialVerified,
                    state.AzureDevOpsSavedQueryConfirmed,
                    state.AiCredentialConfigured,
                    state.AiCredentialVerified,
                    state.AiModelConfigured,
                    state.RequiredAiCredentialSlot?.ToString(),
                    state.RuntimeActivationCurrent,
                    state.SetupComplete,
                    state.Progress.LastVisitedStep?.ToString(),
                    state.Progress.UpdatedAtUtc));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .WithSummary("Read derived setup readiness")
            .WithDescription("Admin and Viewer. Every readiness field is derived from authoritative persistence and runtime state.")
            .Produces<SetupStatusResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapGet("/api/setup/defaults", (OnboardingSetupService onboarding) =>
                Results.Ok(onboarding.GetDefaults()))
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .WithSummary("Read authoritative onboarding defaults and required Admin fields")
            .WithDescription("Admin and Viewer. Values are product-agnostic backend defaults only; null fields require Admin input.")
            .Produces<OnboardingDefaults>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapGet("/api/setup/profile-draft", async (
                OnboardingSetupService onboarding,
                CancellationToken cancellationToken) =>
            {
                var draft = await onboarding.GetDraftAsync(cancellationToken);
                return Results.Ok(ToDraftState(draft, onboarding));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .WithSummary("Read the singleton onboarding profile draft")
            .WithDescription("Admin and Viewer. The draft is safe setup input only and is never a runtime profile.")
            .Produces<OnboardingDraftStateResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/setup/profile-draft/initialize", async (
                ClaimsPrincipal principal,
                OnboardingSetupService onboarding,
                CancellationToken cancellationToken) =>
            {
                var result = await onboarding.InitializeDraftAsync(CurrentActor(principal), cancellationToken);
                return DraftMutationResult(result, onboarding);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Initialize the singleton onboarding draft from server defaults")
            .WithDescription("Admin and antiforgery token required. Existing drafts are returned unchanged; unavailable after profile creation.")
            .Produces<OnboardingDraftStateResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapPut("/api/setup/profile-draft", async (
                OnboardingDraftUpdateRequest request,
                ClaimsPrincipal principal,
                OnboardingSetupService onboarding,
                CancellationToken cancellationToken) =>
            {
                if (request.Values is null)
                    return InvalidDraft();
                var result = await onboarding.UpdateDraftAsync(request.ExpectedRevision,
                    request.Values, CurrentActor(principal), cancellationToken);
                return DraftMutationResult(result, onboarding);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Atomically update supported onboarding profile and policy input")
            .WithDescription("Admin and antiforgery token required. The integer revision prevents lost updates; integration authority is not accepted here.")
            .Accepts<OnboardingDraftUpdateRequest>("application/json")
            .Produces<OnboardingDraftStateResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapPut("/api/setup/progress", async (
                SetupProgressRequest request,
                ClaimsPrincipal principal,
                ISetupProgressRepository progress,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                if (!Enum.TryParse<SetupStep>(request.Step, ignoreCase: false, out var step) ||
                    !Enum.IsDefined(step))
                    return Results.BadRequest(new ApiErrorResponse(
                        "InvalidSetupStep", "The setup step identifier is not supported."));
                await progress.RecordAsync(step, clock.UtcNow.ToUniversalTime(),
                    CurrentActor(principal), cancellationToken);
                var state = await progress.GetAsync(cancellationToken);
                return Results.Ok(new SetupProgressResponse(state.LastVisitedStep?.ToString(), state.UpdatedAtUtc));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Record setup wizard navigation progress")
            .WithDescription("Admin and antiforgery token required. This records navigation only and never marks a setup component complete.")
            .Accepts<SetupProgressRequest>("application/json")
            .Produces<SetupProgressResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/setup/finalize", async (
                SetupFinalizeRequest request,
                ClaimsPrincipal principal,
                OnboardingSetupService onboarding,
                DeploymentConfigurationState runtime,
                CancellationToken cancellationToken) =>
            {
                var result = await onboarding.FinalizeAsync(request.ExpectedDraftRevision,
                    CurrentActor(principal), cancellationToken);
                return FinalizationResult(result, runtime);
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .WithSummary("Create and activate the singleton profile from confirmed setup")
            .WithDescription("Admin and antiforgery token required. The server reconstructs ADO, query, AI, credentials, and draft state and atomically consumes all setup staging.")
            .Accepts<SetupFinalizeRequest>("application/json")
            .Produces<ProfileStateResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static ProfileEditableConfiguration ToEditable(ProfileWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Schedule is null || request.Processing is null || request.AiRuntime is null ||
            request.Audit is null || request.IntakeState is null || request.Policy is null ||
            request.Processing.ContentLimits is null || request.Processing.AttachmentLimits is null)
            throw new ProfileRequestValidationException();
        if (request.Policy.Criteria is null || request.Policy.Criteria.Any(item =>
                item.Na?.Allowed is null || item.Na.RequiresExplanation is null ||
                item.Applicability?.Trim().ToLowerInvariant() is not ("required" or "contextual")))
            throw new ProfileRequestValidationException();
        if (request.Exclusions?.Any(item =>
                !string.Equals(item.Operator, "equalsAny", StringComparison.OrdinalIgnoreCase)) == true)
            throw new ProfileRequestValidationException();
        if (!Uri.TryCreate(request.PolicyUrl, UriKind.Absolute, out var policyUrl) ||
            policyUrl.Scheme is not ("http" or "https")) throw new ProfileRequestValidationException();
        if (!TimeSpan.TryParseExact(request.Schedule?.InitialLookback, "c", CultureInfo.InvariantCulture,
                out var lookback) || lookback <= TimeSpan.Zero) throw new ProfileRequestValidationException();
        var schedule = new ScheduleConfiguration(
            request.Schedule.Enabled,
            request.Schedule.Expression?.Trim() ?? string.Empty,
            request.Schedule.Timezone?.Trim() ?? string.Empty,
            lookback);
        ValidateSchedule(schedule);
        var mode = request.Processing?.ExecutionMode?.Trim().ToUpperInvariant() switch
        {
            "DRY_RUN" => ExecutionMode.DryRun,
            "LIVE" => ExecutionMode.Live,
            _ => throw new ProfileRequestValidationException()
        };
        var policy = new IntakePolicy(
            new PolicyIdentity(request.Policy?.Id?.Trim() ?? string.Empty,
                request.Policy?.Version?.Trim() ?? string.Empty),
            request.Policy?.Criteria?.Select(item => new IntakeCriterion(
                item.Id?.Trim() ?? string.Empty,
                item.DisplayName?.Trim() ?? string.Empty,
                item.Description?.Trim() ?? string.Empty,
                item.Applicability?.Trim().ToLowerInvariant() == "contextual"
                    ? CriterionApplicability.Contextual
                    : CriterionApplicability.Required,
                new NotApplicablePolicy(item.Na?.Allowed ?? false, item.Na?.RequiresExplanation ?? false),
                item.EvaluationGuidance?.Trim() ?? string.Empty)).ToArray() ?? []);
        return new ProfileEditableConfiguration(
            string.IsNullOrWhiteSpace(request.ProfileVersion) ? null : request.ProfileVersion.Trim(),
            policyUrl,
            new IntakeStateConfiguration(request.IntakeState?.ValidatedTag?.Trim() ?? string.Empty,
                request.IntakeState?.IncompleteTag?.Trim() ?? string.Empty),
            request.AiRuntime?.TimeoutSeconds ?? 0,
            request.AiRuntime?.Pricing?.Select(item => new ModelPricing(
                item.Provider?.Trim().ToLowerInvariant() ?? string.Empty,
                item.Model?.Trim() ?? string.Empty,
                item.InputPerMillionTokens ?? -1,
                item.OutputPerMillionTokens ?? -1,
                item.Currency?.Trim().ToUpperInvariant() ?? string.Empty,
                string.IsNullOrWhiteSpace(item.Identity) ? null : item.Identity.Trim())).ToArray() ?? [],
            schedule,
            new ProcessingConfiguration(mode,
                request.Processing.Concurrency ?? 0,
                request.Processing.Retries ?? -1,
                new ContentLimits(
                    request.Processing.ContentLimits?.MaximumTotalCharacters ?? 0,
                    request.Processing.ContentLimits?.MaximumComments ?? 0,
                    request.Processing.ContentLimits?.MaximumExtractedTextCharacters ?? 0),
                new AttachmentLimits(
                    request.Processing.AttachmentLimits?.MaximumCount ?? 0,
                    request.Processing.AttachmentLimits?.MaximumBytesPerAttachment ?? 0,
                    request.Processing.AttachmentLimits?.MaximumAggregateBytes ?? 0,
                    request.Processing.AttachmentLimits?.MaximumPdfPages ?? 0,
                    request.Processing.AttachmentLimits?.MaximumImageCount ?? 0,
                    request.Processing.AttachmentLimits?.MaximumImageBytes ?? 0,
                    request.Processing.AttachmentLimits?.MaximumCsvRows ?? 0,
                    request.Processing.AttachmentLimits?.MaximumStructuredTextDepth ?? 0)),
            new AuditConfiguration(request.Audit?.RetentionDays ?? 0),
            request.Exclusions?.Select(item => new ExclusionRule(
                item.Id?.Trim() ?? string.Empty,
                item.Field?.Trim() ?? string.Empty,
                ExclusionOperator.EqualsAny,
                item.Values?.Select(value => value.Trim()).ToArray() ?? [])).ToArray() ?? [],
            policy);
    }

    private static void ValidateSchedule(ScheduleConfiguration schedule)
    {
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
            if (!schedule.Enabled) return;
            var cron = CronExpression.Parse(schedule.Expression, CronFormat.IncludeSeconds);
            if (cron.GetNextOccurrence(DateTimeOffset.UtcNow, timezone, inclusive: false) is null)
                throw new ProfileRequestValidationException();
        }
        catch (ProfileRequestValidationException) { throw; }
        catch (Exception exception) when (exception is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ProfileRequestValidationException();
        }
    }

    private static ProfileStateResponse ToState(
        ProfileConfigurationSnapshot? snapshot, DeploymentConfigurationState runtime)
    {
        if (snapshot is null)
            return new ProfileStateResponse(false, null, null, null, null, null, null, null, null, null, null, null,
                new ProfileActivationResponse(false, runtime.RestartRequired, runtime.AiRuntimeActivationPending,
                    "No profile is persisted.")
                { Status = "noActiveGeneration" });
        var configuration = snapshot.Configuration;
        return new ProfileStateResponse(
            true,
            snapshot.Revision,
            configuration.Profile.Identity.Id,
            configuration.Profile.Identity.Version,
            new PolicyResponse(configuration.Policy.Identity.Id, configuration.Policy.Identity.Version,
                configuration.PolicyFingerprint, configuration.Profile.IntakePolicy.Url.AbsoluteUri,
                configuration.Policy.Criteria.Select(item => new PolicyCriterionResponse(
                    item.Id, item.DisplayName, item.Description,
                    item.Applicability == CriterionApplicability.Required ? "required" : "contextual",
                    new NotApplicableResponse(item.NotApplicable.Allowed, item.NotApplicable.RequiresExplanation),
                    item.EvaluationGuidance)).ToArray()),
            new AzureDevOpsProfileResponse(configuration.Profile.Ado.OrganizationUrl.AbsoluteUri,
                configuration.Profile.Ado.Project, configuration.Profile.Ado.SavedQueryId),
            new AiProfileResponse(configuration.Profile.Ai.Provider, configuration.Profile.Ai.Model,
                configuration.Profile.Ai.TimeoutSeconds, configuration.Profile.Ai.Pricing.Select(ToPricing).ToArray()),
            new IntakeStateResponse(configuration.Profile.IntakeState.ValidatedTag,
                configuration.Profile.IntakeState.IncompleteTag),
            new ScheduleResponse(configuration.Profile.Schedule.Enabled,
                configuration.Profile.Schedule.Expression, configuration.Profile.Schedule.Timezone,
                configuration.Profile.Schedule.InitialLookback.ToString("c", CultureInfo.InvariantCulture), false),
            ToProcessing(configuration.Profile.Processing),
            new AuditResponse(configuration.Profile.Audit.RetentionDays),
            configuration.Profile.Exclusions.Select(item => new ExclusionResponse(
                item.Id, item.Field, "equalsAny", item.Values)).ToArray(),
            new ProfileActivationResponse(runtime.RuntimeActivationCurrent, runtime.RestartRequired,
                runtime.AiRuntimeActivationPending,
                runtime.RuntimeActivationCurrent
                    ? "The persisted configuration is active for future executions."
                    : "The persisted configuration is valid but has no successfully activated runtime generation.")
            {
                GenerationId = runtime.ActiveGeneration?.GenerationId,
                Status = runtime.RuntimeActivationCurrent ? "active" : "activationFailed"
            });
    }

    private static ProcessingResponse ToProcessing(ProcessingConfiguration processing) => new(
        processing.ExecutionMode == ExecutionMode.DryRun ? "DRY_RUN" : "LIVE",
        processing.Concurrency,
        processing.Retries,
        new ContentLimitsResponse(processing.ContentLimits.MaximumTotalCharacters,
            processing.ContentLimits.MaximumComments, processing.ContentLimits.MaximumExtractedTextCharacters),
        new AttachmentLimitsResponse(processing.AttachmentLimits.MaximumCount,
            processing.AttachmentLimits.MaximumBytesPerAttachment,
            processing.AttachmentLimits.MaximumAggregateBytes,
            processing.AttachmentLimits.MaximumPdfPages,
            processing.AttachmentLimits.MaximumImageCount,
            processing.AttachmentLimits.MaximumImageBytes,
            processing.AttachmentLimits.MaximumCsvRows,
            processing.AttachmentLimits.MaximumStructuredTextDepth));

    private static ModelPricingResponse ToPricing(ModelPricing pricing) => new(
        pricing.Provider, pricing.Model, pricing.InputPerMillionTokens,
        pricing.OutputPerMillionTokens, pricing.Currency, pricing.Identity);

    private static IResult MutationResult(
        ProfileManagementResult result, DeploymentConfigurationState runtime, bool created) => result.Status switch
        {
            ProfileManagementStatus.Succeeded when created => Results.Json(
                ToState(result.Snapshot, runtime), statusCode: StatusCodes.Status201Created),
            ProfileManagementStatus.Succeeded => Results.Ok(ToState(result.Snapshot, runtime)),
            ProfileManagementStatus.NotFound => Results.NotFound(
                new ApiErrorResponse("ProfileNotConfigured", "No deployment profile is configured.")),
            ProfileManagementStatus.InvalidConfiguration => InvalidConfiguration(),
            ProfileManagementStatus.ProductionNotAuthorized => Results.BadRequest(new ApiErrorResponse(
                "ProductionNotAuthorized", "The current release posture authorizes controlled Dry Run only.")),
            ProfileManagementStatus.SetupIncomplete => Results.Json(new ApiErrorResponse(
                "ProfileSetupIncomplete", "Confirmed ADO and AI setup with current verified credentials is required."),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            ProfileManagementStatus.RuntimeChangeInProgress => Results.Conflict(new ApiErrorResponse(
                "ConfigurationActivationInProgress", "Another configuration activation is in progress.")),
            ProfileManagementStatus.IntegrationValidationRequired => Results.Conflict(new ApiErrorResponse(
                "IntegrationValidationRequired", "Verify changed credentials and reconfirm the saved query or AI model before saving the profile.")),
            ProfileManagementStatus.ActivationFailed when created => Results.Json(
                ToState(result.Snapshot, runtime), statusCode: StatusCodes.Status201Created),
            ProfileManagementStatus.ActivationFailed => Results.Json(new ApiErrorResponse(
                "ConfigurationActivationFailed", "The validated configuration was saved, but activation failed; the previous generation remains active."),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Conflict(new ApiErrorResponse(
                "ProfileConflict", "The singleton profile already exists or the supplied revision is stale."))
        };

    private static OnboardingDraftStateResponse ToDraftState(
        OnboardingProfileDraft? draft, OnboardingSetupService onboarding)
    {
        if (draft is null)
            return new OnboardingDraftStateResponse(false, null, null, null, null, null, null);
        var validation = onboarding.ValidateDraft(draft.Values);
        return new OnboardingDraftStateResponse(true, draft.Revision, draft.Values,
            draft.CreatedAtUtc, draft.UpdatedAtUtc, validation.FieldErrors, validation.SectionErrors);
    }

    private static IResult DraftMutationResult(
        OnboardingDraftPersistenceResult result, OnboardingSetupService onboarding) => result.Status switch
        {
            OnboardingDraftPersistenceStatus.Succeeded => Results.Ok(ToDraftState(result.Draft, onboarding)),
            OnboardingDraftPersistenceStatus.NotFound => Results.NotFound(new ApiErrorResponse(
                "OnboardingDraftNotFound", "Initialize the onboarding profile draft before updating it.")),
            OnboardingDraftPersistenceStatus.ProfileAlreadyExists => Results.Conflict(new ApiErrorResponse(
                "ProfileAlreadyConfigured", "Onboarding draft operations are unavailable after profile creation.")),
            OnboardingDraftPersistenceStatus.InvalidConfiguration => InvalidDraft(result.ValidationErrors),
            _ => Results.Conflict(new ApiErrorResponse(
                "OnboardingDraftConflict", "The onboarding draft revision is stale or the supplied draft is invalid."))
        };

    private static IResult FinalizationResult(
        SetupFinalizationResult result, DeploymentConfigurationState runtime) => result.Status switch
        {
            SetupFinalizationStatus.Succeeded => Results.Json(ToState(result.Snapshot, runtime),
                statusCode: StatusCodes.Status201Created),
            SetupFinalizationStatus.DraftNotFound => Results.NotFound(new ApiErrorResponse(
                "OnboardingDraftNotFound", "Initialize and complete the onboarding profile draft first.")),
            SetupFinalizationStatus.InvalidConfiguration => InvalidDraft(result.ValidationErrors),
            SetupFinalizationStatus.ProductionNotAuthorized => Results.BadRequest(new ApiErrorResponse(
                "ProductionNotAuthorized", "The current release posture authorizes controlled Dry Run only.")),
            SetupFinalizationStatus.SetupIncomplete => Results.Json(new ApiErrorResponse(
                "SetupFinalizationIncomplete", "Current verified ADO, confirmed query, AI model, credentials, Admin, and complete draft are required."),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            SetupFinalizationStatus.RuntimeChangeInProgress => Results.Conflict(new ApiErrorResponse(
                "ConfigurationActivationInProgress", "Another configuration activation is in progress.")),
            _ => Results.Conflict(new ApiErrorResponse(
                "SetupFinalizationConflict", "Setup state changed; refresh the authoritative setup state and retry."))
        };

    private static IResult InvalidConfiguration() => Results.BadRequest(new ApiErrorResponse(
        "InvalidProfileConfiguration", "The supplied profile configuration is invalid."));

    private static IResult InvalidImport() => Results.BadRequest(new ApiErrorResponse(
        "InvalidLegacyImport", "Supply valid, secret-free profile and policy YAML content within the documented size limit."));

    private static IResult InvalidPortableValues() => Results.BadRequest(new ApiErrorResponse(
        "InvalidProfileImport", "The selected file contains invalid Profile & Policy values or malformed nested data."));

    private static async Task<(PortableProfileDocument? Document, IResult? Error)> ReadPortableDocumentAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var content = await reader.ReadToEndAsync(cancellationToken);
        if (content.Length == 0 || content.Length > MaximumPortableDocumentCharacters)
            return (null, InvalidPortableJson());
        try
        {
            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return (null, InvalidPortableJson());
            var root = json.RootElement;
            if (!root.TryGetProperty("format", out var format)) return (null, MissingPortableProperty("format"));
            if (format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), ProfilePortability.Format, StringComparison.Ordinal))
                return (null, Results.BadRequest(new ApiErrorResponse("WrongProfileFormat",
                    "This file is not an Engineering Intake Gate profile.")));
            if (!root.TryGetProperty("version", out var version)) return (null, MissingPortableProperty("version"));
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionValue))
                return (null, InvalidPortableJson());
            if (versionValue > ProfilePortability.Version)
                return (null, Results.BadRequest(new ApiErrorResponse("NewerProfileVersion",
                    "This profile was created by a newer version of Engineering Intake Gate and cannot be imported by this version.")));
            if (versionValue != ProfilePortability.Version)
                return (null, Results.BadRequest(new ApiErrorResponse("UnsupportedProfileVersion",
                    "This profile version is not supported by this version of Engineering Intake Gate.")));
            if (!root.TryGetProperty("exportedAt", out _)) return (null, MissingPortableProperty("exportedAt"));
            if (!root.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
                return (null, MissingPortableProperty("profile"));
            if (!root.TryGetProperty("policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
                return (null, MissingPortableProperty("policy"));
            var document = JsonSerializer.Deserialize<PortableProfileDocument>(content, PortableJsonOptions);
            return document is null || document.Profile is null || document.Policy is null
                ? (null, InvalidPortableJson())
                : (document, null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return (null, InvalidPortableJson());
        }
    }

    private static IResult InvalidPortableJson() => Results.BadRequest(new ApiErrorResponse(
        "InvalidProfileFile", "The selected file is not a valid Engineering Intake Gate profile."));

    private static IResult MissingPortableProperty(string property) => Results.BadRequest(new ApiErrorResponse(
        "MissingProfileProperty", $"The selected profile is missing the required '{property}' property."));

    private static IResult InvalidDraft(InputValidationErrors? validationErrors = null) =>
        Results.BadRequest(new ApiErrorResponse(
            "ValidationFailed", "Some profile and policy fields need attention.",
            FieldErrors: validationErrors?.FieldErrors,
            SectionErrors: validationErrors?.SectionErrors));

    private static AuditActor CurrentActor(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            string.IsNullOrWhiteSpace(principal.Identity?.Name))
            throw new InvalidOperationException("Authenticated user identity is incomplete.");
        return new AuditActor(id, principal.Identity.Name);
    }

    private sealed class ProfileRequestValidationException : Exception;
}

public sealed record ProfileWriteRequest(
    string? ProfileVersion,
    string? PolicyUrl,
    IntakeStateRequest? IntakeState,
    AiRuntimeRequest? AiRuntime,
    ScheduleRequest? Schedule,
    ProcessingRequest? Processing,
    AuditRequest? Audit,
    IReadOnlyList<ExclusionRequest>? Exclusions,
    PolicyRequest? Policy);
public sealed record ProfileUpdateRequest(string? ExpectedRevision, OnboardingProfileDraftValues? Profile);
public sealed record LegacyProfileImportRequest(string? ProfileYaml, string? PolicyYaml);
public sealed record IntakeStateRequest(string? ValidatedTag, string? IncompleteTag);
public sealed record AiRuntimeRequest(int? TimeoutSeconds, IReadOnlyList<ModelPricingRequest>? Pricing);
public sealed record ModelPricingRequest(string? Provider, string? Model, decimal? InputPerMillionTokens,
    decimal? OutputPerMillionTokens, string? Currency, string? Identity);
public sealed record ScheduleRequest(bool Enabled, string? Expression, string? Timezone, string? InitialLookback);
public sealed record ProcessingRequest(string? ExecutionMode, int? Concurrency, int? Retries,
    ContentLimitsRequest? ContentLimits, AttachmentLimitsRequest? AttachmentLimits);
public sealed record ContentLimitsRequest(int? MaximumTotalCharacters, int? MaximumComments,
    int? MaximumExtractedTextCharacters);
public sealed record AttachmentLimitsRequest(int? MaximumCount, long? MaximumBytesPerAttachment,
    long? MaximumAggregateBytes, int? MaximumPdfPages, int? MaximumImageCount,
    long? MaximumImageBytes, int? MaximumCsvRows, int? MaximumStructuredTextDepth);
public sealed record AuditRequest(int? RetentionDays);
public sealed record ExclusionRequest(string? Id, string? Field, string? Operator, IReadOnlyList<string>? Values);
public sealed record PolicyRequest(string? Id, string? Version, IReadOnlyList<PolicyCriterionRequest>? Criteria);
public sealed record PolicyCriterionRequest(string? Id, string? DisplayName, string? Description,
    string? Applicability, NotApplicableRequest? Na, string? EvaluationGuidance);
public sealed record NotApplicableRequest(bool? Allowed, bool? RequiresExplanation);

public sealed record ProfileStateResponse(
    bool Exists,
    string? ConfigurationRevision,
    string? ProfileId,
    string? ProfileVersion,
    PolicyResponse? Policy,
    AzureDevOpsProfileResponse? AzureDevOps,
    AiProfileResponse? Ai,
    IntakeStateResponse? IntakeState,
    ScheduleResponse? Schedule,
    ProcessingResponse? Processing,
    AuditResponse? Audit,
    IReadOnlyList<ExclusionResponse>? Exclusions,
    ProfileActivationResponse Activation);
public sealed record PolicyResponse(string Id, string Version, string Fingerprint, string Url,
    IReadOnlyList<PolicyCriterionResponse> Criteria);
public sealed record PolicyCriterionResponse(string Id, string DisplayName, string Description,
    string Applicability, NotApplicableResponse Na, string EvaluationGuidance);
public sealed record NotApplicableResponse(bool Allowed, bool RequiresExplanation);
public sealed record AzureDevOpsProfileResponse(string OrganizationUrl, string Project, Guid SavedQueryId);
public sealed record AiProfileResponse(string Provider, string Model, int TimeoutSeconds,
    IReadOnlyList<ModelPricingResponse> Pricing);
public sealed record ModelPricingResponse(string Provider, string Model, decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens, string Currency, string? Identity);
public sealed record IntakeStateResponse(string ValidatedTag, string IncompleteTag);
public sealed record ScheduleResponse(bool Enabled, string Expression, string Timezone,
    string InitialLookback, bool ActivationPending);
public sealed record ProcessingResponse(string ExecutionMode, int Concurrency, int Retries,
    ContentLimitsResponse ContentLimits, AttachmentLimitsResponse AttachmentLimits);
public sealed record ContentLimitsResponse(int MaximumTotalCharacters, int MaximumComments,
    int MaximumExtractedTextCharacters);
public sealed record AttachmentLimitsResponse(int MaximumCount, long MaximumBytesPerAttachment,
    long MaximumAggregateBytes, int MaximumPdfPages, int MaximumImageCount,
    long MaximumImageBytes, int MaximumCsvRows, int MaximumStructuredTextDepth);
public sealed record AuditResponse(int RetentionDays);
public sealed record ExclusionResponse(string Id, string Field, string Operator, IReadOnlyList<string> Values);
public sealed record ProfileActivationResponse(bool RuntimeActivationCurrent, bool RestartRequired,
    bool AiRuntimeActivationPending, string Message)
{
    public long? GenerationId { get; init; }
    public string Status { get; init; } = "noActiveGeneration";
}
public sealed record ProfileImportPreviewResponse(PortableProfileDocument Document, bool Complete,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SectionErrors);
public sealed record ProfileImportResponse(bool ProfileReplaced, ProfileStateResponse? Profile,
    OnboardingDraftStateResponse? Draft);
public sealed record SetupStatusResponse(
    bool InfrastructureReady,
    bool AdminConfigured,
    bool ProfileConfigured,
    bool OnboardingDraftExists,
    bool ProfileDetailsComplete,
    bool PolicyDetailsComplete,
    bool AzureDevOpsCredentialConfigured,
    bool AzureDevOpsCredentialVerified,
    bool AzureDevOpsSavedQueryConfirmed,
    bool AiCredentialConfigured,
    bool AiCredentialVerified,
    bool AiModelConfigured,
    string? RequiredAiCredentialSlot,
    bool RuntimeActivationCurrent,
    bool SetupComplete,
    string? LastVisitedStep,
    DateTimeOffset? ProgressUpdatedAtUtc);
public sealed record OnboardingDraftUpdateRequest(int ExpectedRevision, OnboardingProfileDraftValues? Values);
public sealed record OnboardingDraftStateResponse(bool Exists, int? Revision,
    OnboardingProfileDraftValues? Values, DateTimeOffset? CreatedAtUtc, DateTimeOffset? UpdatedAtUtc,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldErrors,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? SectionErrors);
public sealed record SetupProgressRequest(string? Step);
public sealed record SetupProgressResponse(string? LastVisitedStep, DateTimeOffset? UpdatedAtUtc);
public sealed record SetupFinalizeRequest(int ExpectedDraftRevision);

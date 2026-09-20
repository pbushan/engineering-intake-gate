using System.Security.Claims;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;
using IntakeGate.Host.Authentication;

namespace IntakeGate.Host.Operations;

public static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/runs", RunProfileNowAsync)
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<RunExecutionResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/runs/work-items", AnalyzeWorkItemAsync)
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<AnalyzeWorkItemResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Existing numeric route remains supported for CLI/harness compatibility.
        endpoints.MapPost("/api/runs/work-items/{id:int}", AnalyzeNumericWorkItemAsync)
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery()
            .Produces<ManualWorkItemRunResult>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/runs", ListRunsAsync)
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<RunHistoryPageResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        endpoints.MapGet("/api/runs/{runId:guid}", GetRunAsync)
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<RunDetailResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/runs/{runId:guid}/items/{evaluationId}", GetRunItemAsync)
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<RunItemDetailResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/runs/{runId:guid}/items/{evaluationId}/screenshots/{screenshotId}", GetScreenshotAsync)
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> RunProfileNowAsync(
        ClaimsPrincipal principal,
        IRuntimeExecutionFactory runtimeFactory,
        DeploymentConfigurationState configurationState,
        IControlPlaneAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (!configurationState.IsConfigured) return ProfileUnavailable();
        var capture = await runtimeFactory.CaptureAsync(cancellationToken);
        if (!capture.Succeeded) return ConfigurationUnavailable(capture.Failure);
        var execution = capture.Services!;
        var actor = CurrentActor(principal);
        if (!await TryAuditAsync(audit, actor, "RunProfileNowRequested", "Profile",
                execution.Generation.ProfileId, ["configurationGenerationId"], cancellationToken))
            return AuditUnavailable();

        var result = await execution.IncrementalRuns.ExecuteAsync(
            execution.Generation.Configuration, RunTriggerType.ManualIncremental, actor, cancellationToken);
        if (!result.Accepted)
            return Results.Conflict(new ApiErrorResponse(
                "ActiveRunInProgress", "Another run already holds the active profile execution lease."));

        await TryAuditAsync(audit, actor, "RunProfileNowCompleted", "Run",
            result.RunId!.Value.ToString("D"), ["status", "configurationGenerationId"], CancellationToken.None);
        return Results.Ok(new RunExecutionResponse(
            result.RunId.Value,
            result.TriggerType,
            MapStatus(result.Status!.Value),
            result.ExecutionMode,
            execution.Generation.GenerationId,
            $"/api/runs/{result.RunId.Value:D}"));
    }

    private static async Task<IResult> AnalyzeWorkItemAsync(
        AnalyzeWorkItemRequest request,
        ClaimsPrincipal principal,
        IServiceProvider services,
        IRuntimeExecutionFactory runtimeFactory,
        DeploymentConfigurationState configurationState,
        IControlPlaneAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (!configurationState.IsConfigured) return ProfileUnavailable();
        var capture = await runtimeFactory.CaptureAsync(cancellationToken);
        if (!capture.Succeeded) return ConfigurationUnavailable(capture.Failure);
        var generation = capture.Services!.Generation;
        if (!TryResolve(request, generation.Configuration.Profile.Ado, out var id))
            return Results.BadRequest(new ApiErrorResponse("InvalidWorkItemIdentity",
                "Supply exactly one positive workItemId or a supported Azure DevOps work-item URL inside the configured organization and project.",
                FieldErrors: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["workItemIdentity"] = ["Enter a positive work-item ID or a supported Azure DevOps work-item URL for the configured organization and project."]
                }));

        var result = await ExecuteManualAsync(id, principal, services, capture.Services!, audit,
            request.ForceFresh ? AnalysisExecutionMode.ForceFresh : AnalysisExecutionMode.NormalReuseEligible,
            cancellationToken);
        if (result.Error is not null) return result.Error;
        var run = result.Result!;
        var decision = Decision(run);
        return Results.Ok(new AnalyzeWorkItemResponse(
            run.RunId,
            run.EvaluationId,
            run.WorkItemId,
            decision,
            OperationalRunReadService.DecisionLabel(decision),
            run.Eligibility,
            run.ProcessingStatus,
            run.ExecutionMode,
            generation.GenerationId,
            $"/api/runs/{run.RunId:D}",
            run.EvaluationId is null ? null : $"/api/runs/{run.RunId:D}/items/{run.EvaluationId}"));
    }

    private static async Task<IResult> AnalyzeNumericWorkItemAsync(
        int id,
        ClaimsPrincipal principal,
        IServiceProvider services,
        IRuntimeExecutionFactory runtimeFactory,
        DeploymentConfigurationState configurationState,
        IControlPlaneAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
            return Results.BadRequest(new ApiErrorResponse("InvalidWorkItemId", "Work-item ID must be positive."));
        if (!configurationState.IsConfigured) return ProfileUnavailable();
        var capture = await runtimeFactory.CaptureAsync(cancellationToken);
        if (!capture.Succeeded) return ConfigurationUnavailable(capture.Failure);
        var result = await ExecuteManualAsync(id, principal, services, capture.Services!, audit,
            AnalysisExecutionMode.NormalReuseEligible, cancellationToken);
        return result.Error ?? Results.Ok(result.Result);
    }

    private static async Task<(ManualWorkItemRunResult? Result, IResult? Error)> ExecuteManualAsync(
        int id,
        ClaimsPrincipal principal,
        IServiceProvider services,
        RuntimeExecutionServices execution,
        IControlPlaneAuditWriter audit,
        AnalysisExecutionMode analysisExecutionMode,
        CancellationToken cancellationToken)
    {
        var actor = CurrentActor(principal);
        if (!await TryAuditAsync(audit, actor, "AnalyzeWorkItemRequested", "WorkItem",
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["configurationGenerationId"], cancellationToken))
            return (null, AuditUnavailable());

        var configuration = execution.Generation.Configuration;
        var discoveryRepository = services.GetRequiredService<IIncrementalDiscoveryRepository>();
        var clock = services.GetRequiredService<IntakeGate.Application.Time.IClock>();
        var lockId = Guid.NewGuid();
        var lease = await discoveryRepository.TryAcquireRunLeaseAsync(configuration.Profile.Identity.Id, lockId,
            clock.UtcNow.ToUniversalTime(), TimeSpan.FromMinutes(15), cancellationToken);
        if (lease is null)
            return (null, Results.Conflict(new ApiErrorResponse(
                "ActiveRunInProgress", "Another run already holds the active profile execution lease.")));
        try
        {
            var result = await execution.ManualRuns.ExecuteAsync(id, configuration, actor,
                analysisExecutionMode, cancellationToken);
            await TryAuditAsync(audit, actor, "AnalyzeWorkItemCompleted", "Run", result.RunId.ToString("D"),
                ["workItemId", "status", "configurationGenerationId"], CancellationToken.None);
            return (result, null);
        }
        catch (LiveExecutionUnavailableException)
        {
            return (null, ProductionUnavailable());
        }
        finally
        {
            await discoveryRepository.ReleaseRunLeaseAsync(
                configuration.Profile.Identity.Id, lockId, CancellationToken.None);
        }
    }

    private static async Task<IResult> ListRunsAsync(
        int? page,
        int? pageSize,
        DateTimeOffset? startedFromUtc,
        DateTimeOffset? startedToUtc,
        RunTriggerType? invocationType,
        OperationalRunStatus? status,
        string? workItemId,
        OperationalRunReadService service,
        CancellationToken cancellationToken)
    {
        var effectivePage = page ?? 1;
        var effectivePageSize = pageSize ?? 25;
        if (effectivePage <= 0 || effectivePageSize is <= 0 or > 100 ||
            startedFromUtc is not null && startedToUtc is not null && startedFromUtc > startedToUtc ||
            workItemId is { Length: > 32 })
            return Results.BadRequest(new ApiErrorResponse("InvalidRunHistoryQuery",
                "Use page >= 1, pageSize between 1 and 100, a valid UTC window, and a bounded work-item ID."));
        var query = new OperationalRunQuery(effectivePage, effectivePageSize,
            startedFromUtc?.ToUniversalTime(), startedToUtc?.ToUniversalTime(), invocationType, status,
            string.IsNullOrWhiteSpace(workItemId) ? null : workItemId.Trim());
        return Results.Ok(await service.ListAsync(query, cancellationToken));
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        OperationalRunReadService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(runId, cancellationToken);
        return result is null
            ? Results.NotFound(new ApiErrorResponse("RunNotFound", "The requested run was not found."))
            : Results.Ok(result);
    }

    private static async Task<IResult> GetRunItemAsync(
        Guid runId,
        string evaluationId,
        OperationalRunReadService service,
        CancellationToken cancellationToken)
    {
        if (evaluationId.Length is < 1 or > 100)
            return Results.NotFound(new ApiErrorResponse("RunItemNotFound", "The requested run item was not found."));
        var result = await service.GetItemAsync(runId, evaluationId, cancellationToken);
        return result is null
            ? Results.NotFound(new ApiErrorResponse("RunItemNotFound", "The requested run item was not found."))
            : Results.Ok(result);
    }

    private static async Task<IResult> GetScreenshotAsync(
        Guid runId,
        string evaluationId,
        string screenshotId,
        OperationalRunReadService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (evaluationId.Length is < 1 or > 100 || screenshotId.Length != 64 ||
            screenshotId.Any(character => !Uri.IsHexDigit(character)))
            return Results.NotFound(new ApiErrorResponse("ScreenshotNotFound", "The selected evidence screenshot is unavailable or has expired."));
        var screenshot = await service.GetScreenshotAsync(runId, evaluationId, screenshotId, cancellationToken);
        if (screenshot is null)
            return Results.NotFound(new ApiErrorResponse("ScreenshotNotFound", "The selected evidence screenshot is unavailable or has expired."));
        // Retained evidence can expire independently of the browser session. Do not let a client
        // cache extend the effective lifetime beyond the repository's authoritative expiry check.
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(screenshot.Content, screenshot.MediaType, enableRangeProcessing: false);
    }

    private static bool TryResolve(AnalyzeWorkItemRequest request, AzureDevOpsConfiguration ado, out int id)
    {
        id = 0;
        if ((request.WorkItemId is null) == (request.WorkItemUrl is null)) return false;
        if (request.WorkItemId is { } numeric)
        {
            id = numeric;
            return id > 0;
        }
        return AzureDevOpsWorkItemResolver.TryResolve(request.WorkItemUrl, ado.OrganizationUrl, ado.Project, out id);
    }

    private static OperationalDecisionState Decision(ManualWorkItemRunResult result)
    {
        if (result.Eligibility == WorkItemEligibility.NotEligible) return OperationalDecisionState.NotEligible;
        if (result.ProcessingStatus == EvaluationProcessingStatus.Error) return OperationalDecisionState.Error;
        return result.Decision switch
        {
            IntakeDecision.Pass => OperationalDecisionState.Pass,
            IntakeDecision.Fail => OperationalDecisionState.Fail,
            _ => OperationalDecisionState.Error
        };
    }

    private static OperationalRunStatus MapStatus(IncrementalRunStatus status) => status switch
    {
        IncrementalRunStatus.Running => OperationalRunStatus.Running,
        IncrementalRunStatus.Completed => OperationalRunStatus.Completed,
        IncrementalRunStatus.CompletedWithErrors => OperationalRunStatus.CompletedWithErrors,
        IncrementalRunStatus.Error => OperationalRunStatus.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static AuditActor CurrentActor(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            string.IsNullOrWhiteSpace(principal.Identity?.Name))
            throw new InvalidOperationException("The authenticated actor identity is unavailable.");
        return new AuditActor(id, principal.Identity.Name);
    }

    private static async Task<bool> TryAuditAsync(
        IControlPlaneAuditWriter writer,
        AuditActor actor,
        string operation,
        string targetCategory,
        string targetId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.AppendAsync(new ControlPlaneAuditRecord(
                Guid.NewGuid(), DateTimeOffset.UtcNow, actor, operation, targetCategory, targetId, fields),
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static IResult ProfileUnavailable() => Results.Json(new ApiErrorResponse(
        "ProfileNotConfigured", "No deployment profile is configured."),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ConfigurationUnavailable(RuntimeExecutionCaptureFailure? failure) => Results.Json(
        new ApiErrorResponse(
            failure == RuntimeExecutionCaptureFailure.CredentialRevisionUnavailable
                ? "CredentialRevisionUnavailable"
                : "NoActiveConfigurationGeneration",
            failure == RuntimeExecutionCaptureFailure.CredentialRevisionUnavailable
                ? "The active generation's exact credential revision is unavailable."
                : "No validated runtime configuration generation is active."),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ProductionUnavailable() => Results.Conflict(new ApiErrorResponse(
        "ProductionNotAuthorized", "Operational execution is limited to the active controlled Dry Run generation."));

    private static IResult AuditUnavailable() => Results.Json(new ApiErrorResponse(
        "OperationalAuditUnavailable", "The execution request could not be audited safely."),
        statusCode: StatusCodes.Status503ServiceUnavailable);
}

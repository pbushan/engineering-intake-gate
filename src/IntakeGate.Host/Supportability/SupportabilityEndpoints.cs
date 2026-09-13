using IntakeGate.Application.Audit;
using IntakeGate.Host.Authentication;

namespace IntakeGate.Host.Supportability;

public static class SupportabilityEndpoints
{
    public static void MapSupportabilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/support/health", async (
                HttpContext context,
                SupportabilityReadService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetHealthAsync(
                context.User.IsInRole(LocalAuthPolicies.Admin), cancellationToken)))
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<SystemHealthResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapGet("/api/audit", async Task<IResult> (
                int? page,
                int? pageSize,
                DateTimeOffset? occurredFromUtc,
                DateTimeOffset? occurredToUtc,
                string? actor,
                string? operation,
                string? targetCategory,
                string? targetId,
                SupportabilityReadService service,
                CancellationToken cancellationToken) =>
            {
                var effectivePage = page ?? 1;
                var effectivePageSize = pageSize ?? 25;
                if (effectivePage <= 0 || effectivePageSize is <= 0 or > 100 ||
                    occurredFromUtc is not null && occurredToUtc is not null && occurredFromUtc > occurredToUtc ||
                    !Bounded(actor) || !Bounded(operation) || !Bounded(targetCategory) || !Bounded(targetId))
                    return Results.BadRequest(new ApiErrorResponse("InvalidAuditQuery",
                        "Use page >= 1, pageSize between 1 and 100, a valid UTC window, and filters no longer than 128 characters."));
                var query = new ControlPlaneAuditQuery(effectivePage, effectivePageSize,
                    occurredFromUtc?.ToUniversalTime(), occurredToUtc?.ToUniversalTime(),
                    Normalize(actor), Normalize(operation), Normalize(targetCategory), Normalize(targetId));
                return Results.Ok(await service.GetAuditAsync(query, cancellationToken));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<ControlPlaneAuditPageResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);
    }

    private static bool Bounded(string? value) => value is null || value.Length <= 128;
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

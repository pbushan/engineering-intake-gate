using IntakeGate.Host.Authentication;

namespace IntakeGate.Host.Home;

public static class HomeSummaryEndpoints
{
    private static readonly IReadOnlySet<int> SupportedWindows = new HashSet<int> { 7, 30, 90 };

    public static IEndpointRouteBuilder MapHomeSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/home/summary", async Task<IResult> (
                int? windowDays,
                HomeSummaryReadService service,
                CancellationToken cancellationToken) =>
            {
                var window = windowDays ?? 30;
                return SupportedWindows.Contains(window)
                    ? Results.Ok(await service.GetAsync(window, cancellationToken))
                    : Results.BadRequest(new ApiErrorResponse("InvalidHomeSummaryWindow",
                        "windowDays must be exactly 7, 30, or 90."));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<HomeSummaryResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);
        return endpoints;
    }
}

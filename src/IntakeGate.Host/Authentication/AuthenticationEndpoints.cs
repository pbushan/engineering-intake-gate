using System.Security.Claims;
using IntakeGate.Application.Authentication;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IntakeGate.Host.Authentication;

public static class AuthenticationEndpoints
{
    public static RouteHandlerBuilder RequireApiAntiforgery(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .AddEndpointFilter<ApiAntiforgeryFilter>();

    public static void MapLocalAuthenticationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new CsrfTokenResponse(tokens.RequestToken!));
        }).AllowAnonymous()
            .Produces<CsrfTokenResponse>();

        app.MapGet("/api/auth/bootstrap/status", async (ILocalUserRepository users, CancellationToken cancellationToken) =>
            Results.Ok(new BootstrapStatusResponse(!await users.AnyUsersAsync(cancellationToken))))
            .AllowAnonymous()
            .Produces<BootstrapStatusResponse>();

        app.MapPost("/api/auth/bootstrap", async (
                BootstrapRequest request,
                LocalAuthenticationService authentication,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var (user, error) = await authentication.BootstrapAsync(
                    request.Username, request.DisplayName, request.Password, cancellationToken);
                if (error == "BootstrapUnavailable")
                    return Results.Conflict(new { error });
                if (error is not null)
                    return Results.BadRequest(new { error });

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    LocalAuthenticationService.CreatePrincipal(user!),
                    SessionProperties());
                return Results.Created("/api/auth/session", SafeUser(user!));
            })
            .AllowAnonymous()
            .RequireApiAntiforgery()
            .Produces<SafeUserResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        app.MapPost("/api/auth/login", async (
                LoginRequest request,
                LocalAuthenticationService authentication,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var user = await authentication.AuthenticateAsync(request.Username, request.Password, cancellationToken);
                if (user is null)
                    return Results.Json(new { error = "InvalidCredentials" }, statusCode: StatusCodes.Status401Unauthorized);

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    LocalAuthenticationService.CreatePrincipal(user),
                    SessionProperties());
                return Results.Ok(SafeUser(user));
            })
            .AllowAnonymous()
            .RequireApiAntiforgery()
            .Produces<SafeUserResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .RequireApiAntiforgery();

        app.MapGet("/api/auth/session", async (
                ClaimsPrincipal principal,
                ILocalUserRepository users,
                CancellationToken cancellationToken) =>
            {
                var user = await CurrentUserAsync(principal, users, cancellationToken);
                return user is null ? Results.Unauthorized() : Results.Ok(SafeUser(user));
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .Produces<SafeUserResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/auth/password", async (
                ChangePasswordRequest request,
                ClaimsPrincipal principal,
                LocalAuthenticationService authentication,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
                    return Results.Unauthorized();
                var error = await authentication.ChangePasswordAsync(
                    id, request.CurrentPassword, request.NewPassword, cancellationToken);
                if (error is not null) return Results.BadRequest(new { error });

                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })
            .RequireAuthorization(LocalAuthPolicies.Authenticated)
            .RequireApiAntiforgery();

        app.MapGet("/api/users", async (ILocalUserRepository users, CancellationToken cancellationToken) =>
            Results.Ok((await users.ListAsync(cancellationToken)).Select(SafeUser)))
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .Produces<IReadOnlyList<SafeUserResponse>>()
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status403Forbidden);

        app.MapPost("/api/users", async (
                CreateUserRequest request,
                ClaimsPrincipal principal,
                LocalAuthenticationService authentication,
                CancellationToken cancellationToken) =>
            {
                var (user, error) = await authentication.CreateAsync(
                    request.Username, request.DisplayName, request.Password, request.Role,
                    CurrentActor(principal), cancellationToken);
                if (error == "UsernameUnavailable") return Results.Conflict(new { error });
                if (error is not null) return Results.BadRequest(new { error });
                return Results.Created($"/api/users/{user!.Id:D}", SafeUser(user));
            })
            .RequireAuthorization(LocalAuthPolicies.Admin)
            .RequireApiAntiforgery();
    }

    private static AuthenticationProperties SessionProperties() => new()
    {
        AllowRefresh = false,
        IsPersistent = false,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
    };

    private static SafeUserResponse SafeUser(LocalUser user) => new(
        user.Id, user.Username, user.DisplayName, user.Role, user.CreatedAtUtc);

    private static async Task<LocalUser?> CurrentUserAsync(
        ClaimsPrincipal principal,
        ILocalUserRepository users,
        CancellationToken cancellationToken) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await users.GetByIdAsync(id, cancellationToken)
            : null;

    private static AuditActor CurrentActor(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            string.IsNullOrWhiteSpace(principal.Identity?.Name))
            throw new InvalidOperationException("Authenticated user identity is incomplete.");
        return new AuditActor(id, principal.Identity.Name);
    }
}

public sealed record BootstrapRequest(string? Username, string? DisplayName, string? Password);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Username, string? DisplayName, string? Password, LocalUserRole Role);
public sealed record CsrfTokenResponse(string Token);
public sealed record BootstrapStatusResponse(bool Available);
public sealed record SafeUserResponse(Guid Id, string Username, string? DisplayName,
    LocalUserRole Role, DateTimeOffset CreatedAtUtc);

public sealed class ApiAntiforgeryFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var validation = context.HttpContext.Features.Get<IAntiforgeryValidationFeature>();
        if (validation is { IsValid: false }) return InvalidToken();
        if (validation is { IsValid: true }) return await next(context);

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return InvalidToken();
        }

        return await next(context);
    }

    private static JsonHttpResult<ApiErrorResponse> InvalidToken() => TypedResults.Json(
        new ApiErrorResponse("InvalidAntiforgeryToken", "A valid antiforgery cookie and request token are required."),
        statusCode: StatusCodes.Status400BadRequest);
}

using System.Security.Claims;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Authentication;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace IntakeGate.Host.Authentication;

public static class LocalAuthPolicies
{
    public const string Authenticated = "Authenticated";
    public const string Admin = "Admin";
}

public sealed class LocalAuthenticationService(
    ILocalUserRepository users,
    IClock clock,
    IPasswordHasher<LocalUser> passwordHasher)
{
    public async Task<(LocalUser? User, string? Error)> BootstrapAsync(
        string? username,
        string? displayName,
        string? password,
        CancellationToken cancellationToken)
    {
        var validated = ValidateNewUser(username, displayName, password);
        if (validated.Error is not null) return (null, validated.Error);

        var now = clock.UtcNow.ToUniversalTime();
        var candidate = new LocalUser(
            Guid.NewGuid(), validated.Username!, validated.NormalizedUsername!, validated.DisplayName,
            string.Empty, LocalUserRole.Admin, now, now);
        var hash = passwordHasher.HashPassword(candidate, password!);
        var created = await users.TryBootstrapAdminAsync(
            candidate.Id, candidate.Username, candidate.NormalizedUsername, candidate.DisplayName, hash, now,
            NewAudit(candidate, now, "BootstrapAdmin", candidate.Id, ["username", "displayName", "role"]),
            cancellationToken);
        return created ? (candidate with { PasswordHash = hash }, null) : (null, "BootstrapUnavailable");
    }

    public async Task<(LocalUser? User, string? Error)> CreateAsync(
        string? username,
        string? displayName,
        string? password,
        LocalUserRole role,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        var validated = ValidateNewUser(username, displayName, password);
        if (validated.Error is not null) return (null, validated.Error);
        if (!Enum.IsDefined(role)) return (null, "InvalidRole");

        var now = clock.UtcNow.ToUniversalTime();
        var candidate = new LocalUser(
            Guid.NewGuid(), validated.Username!, validated.NormalizedUsername!, validated.DisplayName,
            string.Empty, role, now, now);
        var hash = passwordHasher.HashPassword(candidate, password!);
        candidate = candidate with { PasswordHash = hash };
        return await users.TryCreateAsync(candidate,
            new ControlPlaneAuditRecord(Guid.NewGuid(), now, actor, "UserCreated", "LocalUser",
                candidate.Id.ToString("D"), ["username", "displayName", "role"]), cancellationToken)
            ? (candidate, null)
            : (null, "UsernameUnavailable");
    }

    public async Task<LocalUser?> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        var user = await users.FindByNormalizedUsernameAsync(LocalUsername.Normalize(username), cancellationToken);
        if (user is null) return null;

        PasswordVerificationResult verification;
        try
        {
            verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        }
        catch (FormatException)
        {
            return null;
        }

        if (verification == PasswordVerificationResult.Failed) return null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var changedAt = NextPasswordChange(user.PasswordChangedAtUtc, clock.UtcNow);
            var replacement = passwordHasher.HashPassword(user, password);
            if (await users.TryReplacePasswordHashAsync(
                    user.Id, user.PasswordHash, replacement, changedAt,
                    NewAudit(user, changedAt, "PasswordRehashed", user.Id, ["passwordHash", "passwordChangedAtUtc"]),
                    cancellationToken))
            {
                user = user with { PasswordHash = replacement, PasswordChangedAtUtc = changedAt };
            }
            else
            {
                return null;
            }
        }

        return user;
    }

    public async Task<string?> ChangePasswordAsync(
        Guid userId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(currentPassword)) return "CurrentPasswordInvalid";
        if (!IsValidPassword(newPassword)) return "InvalidNewPassword";

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null) return "CurrentPasswordInvalid";
        PasswordVerificationResult verification;
        try
        {
            verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);
        }
        catch (FormatException)
        {
            return "CurrentPasswordInvalid";
        }

        if (verification == PasswordVerificationResult.Failed) return "CurrentPasswordInvalid";
        var replacement = passwordHasher.HashPassword(user, newPassword!);
        var changedAt = NextPasswordChange(user.PasswordChangedAtUtc, clock.UtcNow);
        var changed = await users.TryReplacePasswordHashAsync(
            user.Id, user.PasswordHash, replacement,
            changedAt,
            NewAudit(user, changedAt, "PasswordChanged", user.Id, ["authenticationCredential", "passwordChangedAtUtc"]),
            cancellationToken);
        return changed ? null : "PasswordChangeConflict";
    }

    public static ClaimsPrincipal CreatePrincipal(LocalUser user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("password_changed_at_utc", user.PasswordChangedAtUtc.ToUniversalTime().ToString("O"))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static (string? Username, string? NormalizedUsername, string? DisplayName, string? Error)
        ValidateNewUser(string? username, string? displayName, string? password)
    {
        var trimmedUsername = username?.Trim();
        if (trimmedUsername is null || trimmedUsername.Length is < 3 or > 64)
            return (null, null, null, "InvalidUsername");
        if (!IsValidPassword(password)) return (null, null, null, "InvalidPassword");

        var trimmedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (trimmedDisplayName?.Length > 100) return (null, null, null, "InvalidDisplayName");
        return (trimmedUsername, LocalUsername.Normalize(trimmedUsername), trimmedDisplayName, null);
    }

    private static bool IsValidPassword(string? password) => password is { Length: >= 12 and <= 256 };

    private static DateTimeOffset NextPasswordChange(DateTimeOffset previous, DateTimeOffset current)
    {
        var utc = current.ToUniversalTime();
        return utc > previous ? utc : previous.AddTicks(1);
    }

    private static ControlPlaneAuditRecord NewAudit(
        LocalUser actor,
        DateTimeOffset at,
        string operation,
        Guid target,
        IReadOnlyList<string> changedFields) => new(
        Guid.NewGuid(), at.ToUniversalTime(), new AuditActor(actor.Id, actor.Username), operation,
        "LocalUser", target.ToString("D"), changedFields);
}

public sealed class LocalCookieAuthenticationEvents(ILocalUserRepository users) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var passwordChangedAt = context.Principal?.FindFirstValue("password_changed_at_utc");
        if (!Guid.TryParse(idValue, out var id) || passwordChangedAt is null)
        {
            await RejectAsync(context);
            return;
        }

        var user = await users.GetByIdAsync(id, context.HttpContext.RequestAborted);
        if (user is null ||
            !string.Equals(passwordChangedAt, user.PasswordChangedAtUtc.ToUniversalTime().ToString("O"), StringComparison.Ordinal) ||
            !context.Principal!.IsInRole(user.Role.ToString()))
        {
            await RejectAsync(context);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

namespace IntakeGate.Application.Authentication;

public enum LocalUserRole
{
    Admin,
    Viewer
}

public sealed record LocalUser(
    Guid Id,
    string Username,
    string NormalizedUsername,
    string? DisplayName,
    string PasswordHash,
    LocalUserRole Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset PasswordChangedAtUtc);

public static class LocalUsername
{
    public static string Normalize(string username) => username.Trim().ToUpperInvariant();
}

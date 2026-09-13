using IntakeGate.Application.Authentication;
using IntakeGate.Application.Audit;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteLocalUserRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-users-{Guid.NewGuid():N}");

    [Fact]
    public async Task AUTH_001_ConcurrentBootstrapCreatesExactlyOneAdmin()
    {
        var path = Path.Combine(directory, "bootstrap.db");
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var repository = new SqliteLocalUserRepository(path);
        var now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            repository.TryBootstrapAdminAsync(
                Guid.NewGuid(), $"admin-{index}", $"ADMIN-{index}", null, $"hash-{index}", now,
                Audit($"admin-{index}", now, "BootstrapAdmin"))));

        Assert.Single(results, result => result);
        var users = await repository.ListAsync();
        var user = Assert.Single(users);
        Assert.Equal(LocalUserRole.Admin, user.Role);
    }

    [Fact]
    public async Task AUTH_002_NormalizedUsernameUniquenessIsDatabaseEnforced()
    {
        var path = Path.Combine(directory, "unique.db");
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var repository = new SqliteLocalUserRepository(path);
        var now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
        Assert.True(await repository.TryBootstrapAdminAsync(
            Guid.NewGuid(), "Admin", "ADMIN", null, "hash-one", now,
            Audit("Admin", now, "BootstrapAdmin")));

        var created = await repository.TryCreateAsync(new LocalUser(
                Guid.NewGuid(), "admin", "ADMIN", null, "hash-two", LocalUserRole.Viewer, now, now),
            Audit("Admin", now, "UserCreated"));

        Assert.False(created);
        Assert.Single(await repository.ListAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static ControlPlaneAuditRecord Audit(string username, DateTimeOffset at, string operation) =>
        new(Guid.NewGuid(), at, new AuditActor(Guid.NewGuid(), username), operation, "LocalUser", Guid.NewGuid().ToString("D"), []);
}

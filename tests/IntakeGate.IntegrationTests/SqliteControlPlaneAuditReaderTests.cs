using IntakeGate.Application.Audit;
using IntakeGate.Infrastructure.Persistence;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteControlPlaneAuditReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-support-audit-{Guid.NewGuid():N}");

    [Fact]
    public async Task SUP_014_SUP_015_SUP_016_AuditIsPagedNewestFirstAndServerFiltered()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "audit.db");
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var repository = new SqliteAuditRepository(database);
        var admin = new AuditActor(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "admin");
        var viewer = new AuditActor(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "viewer");
        await repository.AppendAsync(Record("2026-09-13T10:00:00Z", admin, "ProfileUpdated", "Profile", "singleton"));
        await repository.AppendAsync(Record("2026-09-13T11:00:00Z", viewer, "SetupProgressRecorded", "Setup", "ReviewFinish"));
        await repository.AppendAsync(Record("2026-09-13T12:00:00Z", admin, "AzureDevOpsConnectionVerificationSucceeded", "AzureDevOps", "connection"));

        var page = await repository.ListControlPlaneAsync(new ControlPlaneAuditQuery(
            1, 2, null, null, null, null, null, null));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["AzureDevOpsConnectionVerificationSucceeded", "SetupProgressRecorded"],
            page.Records.Select(record => record.Operation));

        var filtered = await repository.ListControlPlaneAsync(new ControlPlaneAuditQuery(
            1, 10, DateTimeOffset.Parse("2026-09-13T11:30:00Z"), null,
            "ADMIN", "AzureDevOpsConnectionVerificationSucceeded", "azuredevops", "CONNECTION"));
        Assert.Equal("AzureDevOpsConnectionVerificationSucceeded", Assert.Single(filtered.Records).Operation);
    }

    private static ControlPlaneAuditRecord Record(
        string at,
        AuditActor actor,
        string operation,
        string category,
        string target) => new(Guid.NewGuid(), DateTimeOffset.Parse(at), actor, operation, category, target,
        ["verificationStatus"]);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

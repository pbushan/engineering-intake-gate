using IntakeGate.Application.Audit;
using IntakeGate.Application.Setup;
using IntakeGate.Infrastructure.Persistence;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteOnboardingProfileDraftRepositoryTests : IDisposable
{
    private readonly string database = Path.Combine(Path.GetTempPath(),
        $"intake-gate-onboarding-{Guid.NewGuid():N}.db");
    private static readonly AuditActor Actor = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "onboarding-admin");

    [Fact]
    public async Task SETUP_4B0_DefaultsSeedOnceAndDraftSurvivesRepositoryRestart()
    {
        await new SqliteDatabaseMigrator(database).MigrateAsync();
        var first = new SqliteOnboardingProfileDraftRepository(database);
        var defaults = AuthoritativeOnboardingDefaults.Create().Values;

        var initialized = await first.InitializeAsync(defaults, Actor,
            DateTimeOffset.Parse("2026-09-12T12:00:00Z"));
        var repeated = await first.InitializeAsync(defaults with { ProfileVersion = "changed-default" }, Actor,
            DateTimeOffset.Parse("2026-09-12T13:00:00Z"));
        var restarted = await new SqliteOnboardingProfileDraftRepository(database).GetAsync();

        Assert.Equal(OnboardingDraftPersistenceStatus.Succeeded, initialized.Status);
        Assert.Equal(1, repeated.Draft!.Revision);
        Assert.Null(repeated.Draft.Values.ProfileVersion);
        Assert.NotNull(restarted);
        Assert.Equal(1, restarted.Revision);
        Assert.Equal(defaults.AiRuntime!.TimeoutSeconds, restarted.Values.AiRuntime!.TimeoutSeconds);
        Assert.Equal(defaults.Processing!.ExecutionMode, restarted.Values.Processing!.ExecutionMode);
        Assert.Equal(defaults.Processing.AttachmentLimits!.MaximumImageCount,
            restarted.Values.Processing.AttachmentLimits!.MaximumImageCount);
        Assert.Empty(restarted.Values.Exclusions!);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(database);
        File.Delete(database + "-shm");
        File.Delete(database + "-wal");
    }
}

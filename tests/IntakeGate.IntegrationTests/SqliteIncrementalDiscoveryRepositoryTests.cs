using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Discovery;
using IntakeGate.Infrastructure.Persistence;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class SqliteIncrementalDiscoveryRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"intake-gate-discovery-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    [Fact]
    public async Task DISC_003_DISC_004_AC_17_RegistrationAndCheckpointPersistAcrossRestartInOneDurableBoundary()
    {
        var path = Path.Combine(directory, "operational.db");
        var first = new SqliteIncrementalDiscoveryRepository(path);
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var runId = Guid.NewGuid();

        await first.RegisterAndAdvanceCheckpointAsync("profile", runId,
            [new DiscoveryRegistrationRequest(101, DiscoverySelectionReason.NewlyDiscovered, RegisteredWorkState.Pending),
             new DiscoveryRegistrationRequest(102, DiscoverySelectionReason.IncompleteReevaluation, RegisteredWorkState.Pending)], now);

        var restarted = new SqliteIncrementalDiscoveryRepository(path);
        var checkpoint = await restarted.GetCheckpointAsync("profile");
        var registrations = await restarted.GetProcessableRegistrationsAsync("profile");

        Assert.Equal(now, checkpoint!.AdvancedAtUtc);
        Assert.Equal(runId, checkpoint.DiscoveryRunId);
        Assert.Equal([101, 102], registrations.Select(item => item.WorkItemId));
        Assert.All(registrations, item => Assert.Equal(RegisteredWorkState.Pending, item.ProcessingState));
        Assert.All(registrations, item => Assert.Equal(TimeSpan.Zero, item.DiscoveredAtUtc.Offset));
    }

    [Fact]
    public async Task DISC_007_SAFE_003_E17_OnlyOneProcessCanHoldLiveLeaseAndExpiredLeaseIsRecoverable()
    {
        var path = Path.Combine(directory, "locks.db");
        var first = new SqliteIncrementalDiscoveryRepository(path);
        var second = new SqliteIncrementalDiscoveryRepository(path);
        await new SqliteDatabaseMigrator(path).MigrateAsync();

        var holder = await first.TryAcquireRunLeaseAsync("profile", Guid.NewGuid(), now, TimeSpan.FromMinutes(1));
        var rejected = await second.TryAcquireRunLeaseAsync("profile", Guid.NewGuid(), now, TimeSpan.FromMinutes(1));
        var recovered = await second.TryAcquireRunLeaseAsync("profile", Guid.NewGuid(), now.AddMinutes(2), TimeSpan.FromMinutes(1));

        Assert.NotNull(holder);
        Assert.Null(rejected);
        Assert.NotNull(recovered);
        Assert.Equal(TimeSpan.Zero, recovered!.AcquiredAtUtc.Offset);
    }

    [Fact]
    public async Task AUD_002_UTC_IncrementalRunAuditRetainsSafeAggregateFields()
    {
        var path = Path.Combine(directory, "audit.db");
        var repository = new SqliteIncrementalDiscoveryRepository(path);
        await new SqliteDatabaseMigrator(path).MigrateAsync();
        var record = new IncrementalRunAuditRecord(Guid.NewGuid(), RunTriggerType.ManualIncremental, now, now.AddSeconds(2),
            ExecutionMode.DryRun, "profile", Guid.NewGuid(), now.AddDays(-1), now, 3, 1, 1, 2, 1, 0, 1, 0,
            new TokenUsage(4, 2, 6), new EstimatedCost(0.01m, "USD"), IncrementalRunStatus.CompletedWithErrors, "WorkItemReadFailed");

        await repository.SaveRunAuditAsync(record);
        var restored = await repository.GetRunAuditAsync(record.RunId);

        Assert.Equal(record, restored);
        Assert.Equal(TimeSpan.Zero, restored!.StartedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, restored.CompletedAtUtc!.Value.Offset);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

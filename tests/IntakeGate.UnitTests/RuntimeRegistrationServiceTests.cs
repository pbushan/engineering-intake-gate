using IntakeGate.Application;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Domain;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class RuntimeRegistrationServiceTests
{
    [Fact]
    public async Task DB_002_RegisterAsync_UsesRepositoryAndUtcClock()
    {
        var expectedTime = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.Zero);
        var repository = new RecordingRepository();
        var service = new RuntimeRegistrationService(repository, new FixedClock(expectedTime));
        var instanceId = Guid.NewGuid();

        var result = await service.RegisterAsync(instanceId, "1.2.3");

        Assert.Same(result, repository.AddedRecord);
        Assert.Equal(instanceId, result.InstanceId);
        Assert.Equal(expectedTime, result.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.StartedAtUtc.Offset);
    }

    [Fact]
    public void NFR_003_RuntimeRecord_RejectsNonUtcTimestamp()
    {
        var nonUtc = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.FromHours(-4));

        Assert.Throws<ArgumentException>(() =>
            new ApplicationRuntimeRecord(Guid.NewGuid(), Guid.NewGuid(), nonUtc, "1.0.0"));
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class RecordingRepository : IApplicationRuntimeRepository
    {
        public ApplicationRuntimeRecord? AddedRecord { get; private set; }

        public Task AddAsync(ApplicationRuntimeRecord record, CancellationToken cancellationToken = default)
        {
            AddedRecord = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ApplicationRuntimeRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApplicationRuntimeRecord>>([]);

        public Task<bool> CanAccessAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}

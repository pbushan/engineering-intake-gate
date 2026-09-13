using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Domain;

namespace IntakeGate.Application;

public sealed class RuntimeRegistrationService(
    IApplicationRuntimeRepository repository,
    IClock clock)
{
    public async Task<ApplicationRuntimeRecord> RegisterAsync(
        Guid instanceId,
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        var record = new ApplicationRuntimeRecord(
            Guid.NewGuid(),
            instanceId,
            clock.UtcNow.ToUniversalTime(),
            applicationVersion);

        await repository.AddAsync(record, cancellationToken);
        return record;
    }
}

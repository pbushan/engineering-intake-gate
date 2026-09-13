using IntakeGate.Domain;

namespace IntakeGate.Application.Persistence;

public interface IApplicationRuntimeRepository
{
    Task AddAsync(ApplicationRuntimeRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationRuntimeRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> CanAccessAsync(CancellationToken cancellationToken = default);
}

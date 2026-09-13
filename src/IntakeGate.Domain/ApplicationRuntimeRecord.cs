namespace IntakeGate.Domain;

public sealed record ApplicationRuntimeRecord
{
    public ApplicationRuntimeRecord(
        Guid id,
        Guid instanceId,
        DateTimeOffset startedAtUtc,
        string applicationVersion)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A runtime record ID is required.", nameof(id));
        }

        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("An instance ID is required.", nameof(instanceId));
        }

        if (startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Persisted runtime timestamps must be UTC.", nameof(startedAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);

        Id = id;
        InstanceId = instanceId;
        StartedAtUtc = startedAtUtc;
        ApplicationVersion = applicationVersion;
    }

    public Guid Id { get; }

    public Guid InstanceId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public string ApplicationVersion { get; }
}

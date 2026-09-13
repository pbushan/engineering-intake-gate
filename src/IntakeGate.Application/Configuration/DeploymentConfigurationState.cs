namespace IntakeGate.Application.Configuration;

/// <summary>
/// Process-local view of the persisted active-generation pointer. Executions capture the immutable
/// generation itself and never repeatedly dereference this state.
/// </summary>
public sealed class DeploymentConfigurationState(
    DeploymentConfiguration? configuration)
{
    private readonly object sync = new();
    private RuntimeConfigurationGeneration? activeGeneration;
    private DeploymentConfiguration? activeConfiguration = configuration;
    private TaskCompletionSource change = NewSignal();
    private int activationCurrent;
    private int managementChange;

    public DeploymentConfiguration? Configuration => Volatile.Read(ref activeConfiguration);

    public RuntimeConfigurationGeneration? ActiveGeneration => Volatile.Read(ref activeGeneration);

    public bool IsConfigured => Configuration is not null;

    public bool ExecutionAvailable => ActiveGeneration is not null;

    public bool RestartRequired => false;

    public bool AiRuntimeActivationPending => false;

    public bool RuntimeActivationCurrent => ActiveGeneration is not null && Volatile.Read(ref activationCurrent) == 1;

    public bool TryBeginManagementChange()
    {
        return Interlocked.CompareExchange(ref managementChange, 1, 0) == 0;
    }

    public void CompleteManagementChange(bool restartRequired)
    {
        Interlocked.Exchange(ref managementChange, 0);
    }

    public void Activate(RuntimeConfigurationGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        TaskCompletionSource prior;
        lock (sync)
        {
            Volatile.Write(ref activeGeneration, generation);
            Volatile.Write(ref activeConfiguration, generation.Configuration);
            Volatile.Write(ref activationCurrent, 1);
            prior = change;
            change = NewSignal();
        }
        prior.TrySetResult();
    }

    public void RequireAiRuntimeActivation() => MarkActivationPending();

    public void MarkActivationPending() => Volatile.Write(ref activationCurrent, 0);

    public Task WaitForChangeAsync(long? observedGenerationId, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (activeGeneration?.GenerationId != observedGenerationId) return Task.CompletedTask;
            return change.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ProfileNotConfiguredException : InvalidOperationException
{
    public ProfileNotConfiguredException()
        : base("No deployment profile is configured. Explicitly import a legacy profile before running profile-dependent operations.")
    {
    }
}

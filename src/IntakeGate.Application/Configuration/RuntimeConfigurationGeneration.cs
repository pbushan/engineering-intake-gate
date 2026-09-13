using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Application.Configuration;

/// <summary>A secret-free, immutable configuration captured by an execution.</summary>
public sealed record RuntimeConfigurationGeneration(
    long GenerationId,
    string ProfileId,
    DeploymentConfiguration Configuration,
    string ConfigurationFingerprint,
    RuntimeCredentialBinding AzureDevOpsCredential,
    RuntimeCredentialBinding AiCredential,
    DateTimeOffset CreatedAtUtc);

public sealed record RuntimeCredentialBinding(
    CredentialSlot Slot,
    SecretSourceKind SourceKind,
    DateTimeOffset Revision);

public enum RuntimeActivationFailure
{
    None,
    ProfileNotConfigured,
    ConfigurationInvalid,
    ProductionNotAuthorized,
    AzureDevOpsNotReady,
    AiNotReady,
    CredentialNotReady,
    PersistenceFailure
}

public sealed record RuntimeActivationResult(
    bool Succeeded,
    RuntimeConfigurationGeneration? Generation,
    RuntimeActivationFailure Failure = RuntimeActivationFailure.None);

public interface IRuntimeConfigurationGenerationRepository
{
    Task<RuntimeConfigurationGeneration?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<RuntimeConfigurationGeneration?> GetAsync(long generationId, CancellationToken cancellationToken = default);

    /// <summary>Atomically appends an immutable generation, moves the active pointer, and audits it.</summary>
    Task<RuntimeConfigurationGeneration> ActivateAsync(
        DeploymentConfiguration configuration,
        string configurationFingerprint,
        RuntimeCredentialBinding azureDevOpsCredential,
        RuntimeCredentialBinding aiCredential,
        AuditActor actor,
        DateTimeOffset nowUtc,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeConfigurationActivator
{
    Task<RuntimeActivationResult> ActivateAuthoritativeAsync(
        AuditActor actor,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeAzureDevOpsAdapters(IWorkItemSource Source, IWorkItemWriter Writer);

public interface IRuntimeAzureDevOpsAdapterFactory
{
    RuntimeAzureDevOpsAdapters Create(DeploymentConfiguration configuration, SecretValue credential);
}

public interface IRuntimeAiProviderFactory
{
    IIntakeAiProvider Create(DeploymentConfiguration configuration, SecretValue credential);
}

/// <summary>
/// Validates the complete persisted authority and activates it only after its exact credential
/// revisions and confirmed provider boundaries are coherent.
/// </summary>
public sealed class RuntimeConfigurationActivator(
    ISingletonProfileRepository profiles,
    IAzureDevOpsConfigurationRepository azureDevOps,
    IAiConfigurationRepository ai,
    ISecretStore secrets,
    IRuntimeConfigurationGenerationRepository generations,
    IControlPlaneAuditWriter audit,
    DeploymentConfigurationState state,
    IClock clock) : IRuntimeConfigurationActivator
{
    public async Task<RuntimeActivationResult> ActivateAuthoritativeAsync(
        AuditActor actor,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default)
    {
        var configuration = await profiles.LoadAsync(cancellationToken);
        if (configuration is null)
            return await FailedAsync(actor, RuntimeActivationFailure.ProfileNotConfigured, null, cancellationToken);
        if (configuration.Profile.Processing.ExecutionMode == ExecutionMode.Live)
        {
            state.MarkActivationPending();
            return await FailedAsync(actor, RuntimeActivationFailure.ProductionNotAuthorized,
                configuration.Profile.Identity.Id, cancellationToken);
        }

        var adoState = await azureDevOps.GetStateAsync(cancellationToken);
        if (adoState is null || !adoState.QueryConfirmed ||
            !string.Equals(adoState.ProfileId, configuration.Profile.Identity.Id, StringComparison.Ordinal))
        {
            state.MarkActivationPending();
            return await FailedAsync(actor, RuntimeActivationFailure.AzureDevOpsNotReady,
                configuration.Profile.Identity.Id, cancellationToken);
        }

        var aiState = await ai.GetAsync(cancellationToken);
        if (!aiState.ProfileConfigured || !aiState.ModelConfirmed ||
            !string.Equals(aiState.ProfileId, configuration.Profile.Identity.Id, StringComparison.Ordinal) ||
            !string.Equals(aiState.Provider, configuration.Profile.Ai.Provider, StringComparison.Ordinal) ||
            !string.Equals(aiState.Model, configuration.Profile.Ai.Model, StringComparison.Ordinal))
        {
            state.MarkActivationPending();
            return await FailedAsync(actor, RuntimeActivationFailure.AiNotReady,
                configuration.Profile.Identity.Id, cancellationToken);
        }

        var adoMetadata = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var aiSlot = AiProviderNames.CredentialSlot(configuration.Profile.Ai.Provider);
        var aiMetadata = await secrets.GetMetadataAsync(aiSlot, cancellationToken);
        if (!Ready(adoMetadata) || !Ready(aiMetadata) || aiState.CredentialUpdatedAtUtc != aiMetadata.UpdatedAtUtc)
        {
            state.MarkActivationPending();
            return await FailedAsync(actor, RuntimeActivationFailure.CredentialNotReady,
                configuration.Profile.Identity.Id, cancellationToken);
        }

        try
        {
            var fingerprint = RuntimeConfigurationFingerprint.Create(
                configuration, adoMetadata.UpdatedAtUtc!.Value, aiMetadata.UpdatedAtUtc!.Value);
            var generation = await generations.ActivateAsync(
                configuration,
                fingerprint,
                new RuntimeCredentialBinding(CredentialSlot.AzureDevOps,
                    adoMetadata.SourceKind!.Value, adoMetadata.UpdatedAtUtc.Value),
                new RuntimeCredentialBinding(aiSlot,
                    aiMetadata.SourceKind!.Value, aiMetadata.UpdatedAtUtc.Value),
                actor,
                clock.UtcNow.ToUniversalTime(),
                changedFields,
                cancellationToken);
            state.Activate(generation);
            return new(true, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            state.MarkActivationPending();
            return await FailedAsync(actor, RuntimeActivationFailure.PersistenceFailure,
                configuration.Profile.Identity.Id, CancellationToken.None);
        }
    }

    private async Task<RuntimeActivationResult> FailedAsync(AuditActor actor,
        RuntimeActivationFailure failure, string? profileId, CancellationToken cancellationToken)
    {
        try
        {
            await audit.AppendAsync(new ControlPlaneAuditRecord(Guid.NewGuid(), clock.UtcNow.ToUniversalTime(), actor,
                $"RuntimeConfigurationActivationFailed{failure}", "RuntimeConfiguration", profileId ?? "profileless",
                ["activationState"]), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        return new(false, null, failure);
    }

    private static bool Ready(CredentialMetadata metadata) =>
        metadata.Configured && metadata.SourceKind is not null && metadata.UpdatedAtUtc is not null &&
        metadata.VerificationStatus == CredentialVerificationStatus.Verified;
}

using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.AiManagement;

namespace IntakeGate.Application.Setup;

public enum SetupStep
{
    Welcome,
    BootstrapAdmin,
    AzureDevOps,
    Ai,
    SystemDefaults,
    Profile,
    SavedQuery,
    OptionalSchedule,
    ReviewFinish
}

public sealed record SetupProgress(SetupStep? LastVisitedStep, DateTimeOffset? UpdatedAtUtc);

public sealed record SetupReadiness(
    bool InfrastructureReady,
    bool AdminConfigured,
    bool ProfileConfigured,
    bool OnboardingDraftExists,
    bool ProfileDetailsComplete,
    bool PolicyDetailsComplete,
    bool AzureDevOpsCredentialConfigured,
    bool AzureDevOpsCredentialVerified,
    bool AzureDevOpsSavedQueryConfirmed,
    bool AiCredentialConfigured,
    bool AiCredentialVerified,
    bool AiModelConfigured,
    CredentialSlot? RequiredAiCredentialSlot,
    bool RuntimeActivationCurrent,
    bool SetupComplete,
    SetupProgress Progress);

public interface ISetupProgressRepository
{
    Task<SetupProgress> GetAsync(CancellationToken cancellationToken = default);
    Task RecordAsync(SetupStep step, DateTimeOffset atUtc, AuditActor actor,
        CancellationToken cancellationToken = default);
}

public sealed class SetupStateService(
    IApplicationRuntimeRepository runtime,
    ILocalUserRepository users,
    ISingletonProfileRepository profiles,
    ISecretStore secrets,
    ISetupProgressRepository progress,
    DeploymentConfigurationState configurationState,
    IAzureDevOpsConfigurationRepository? adoConfigurations = null,
    IAiConfigurationRepository? aiConfigurations = null,
    IAzureDevOpsSetupRepository? adoSetup = null,
    OnboardingSetupService? onboarding = null)
{
    public async Task<SetupReadiness> GetAsync(CancellationToken cancellationToken = default)
    {
        var infrastructureReady = await runtime.CanAccessAsync(cancellationToken);
        var admin = await users.AnyUsersAsync(cancellationToken);
        var authoritativeConfiguration = await profiles.LoadAsync(cancellationToken);
        var profile = authoritativeConfiguration is not null;
        var draft = onboarding is null ? null : await onboarding.GetDraftAsync(cancellationToken);
        var profileDetailsComplete = onboarding?.IsProfileDetailsComplete(draft) == true;
        var policyDetailsComplete = onboarding?.IsPolicyDetailsComplete(draft) == true;
        var ado = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var adoState = adoConfigurations is null
            ? null
            : await adoConfigurations.GetStateAsync(cancellationToken);
        var stagedAdo = profile || adoSetup is null ? null : await adoSetup.GetAsync(cancellationToken);
        var adoVerified = adoConfigurations is null ||
                          ado.VerificationStatus == CredentialVerificationStatus.Verified;
        var queryConfirmed = adoConfigurations is null || adoState?.QueryConfirmed == true ||
                             stagedAdo is
                             {
                                 SavedQueryId: not null, ConfigurationFingerprint: not null,
                                 QueryValidatedAtUtc: not null
                             };
        var aiState = aiConfigurations is null ? null : await aiConfigurations.GetAsync(cancellationToken);
        var configuredProvider = aiState?.Provider ?? configurationState.Configuration?.Profile.Ai.Provider;
        var requiredAi = configuredProvider switch
        {
            AiProviderNames.OpenAi => CredentialSlot.OpenAi,
            AiProviderNames.Anthropic => CredentialSlot.Anthropic,
            _ => (CredentialSlot?)null
        };
        var aiConfigured = requiredAi is { } slot &&
                           (await secrets.GetMetadataAsync(slot, cancellationToken)).Configured;
        var aiMetadata = requiredAi is { } aiSlot
            ? await secrets.GetMetadataAsync(aiSlot, cancellationToken)
            : null;
        var aiVerified = aiMetadata?.VerificationStatus == CredentialVerificationStatus.Verified;
        var aiModelConfigured = aiState?.ModelConfirmed == true &&
                                aiState.CredentialUpdatedAtUtc == aiMetadata?.UpdatedAtUtc;
        var active = configurationState.ActiveGeneration;
        var runtimeActivationCurrent = active is not null && authoritativeConfiguration is not null &&
            ado.UpdatedAtUtc == active.AzureDevOpsCredential.Revision &&
            ado.SourceKind == active.AzureDevOpsCredential.SourceKind &&
            aiMetadata?.UpdatedAtUtc == active.AiCredential.Revision &&
            aiMetadata?.SourceKind == active.AiCredential.SourceKind &&
            string.Equals(active.ConfigurationFingerprint,
                RuntimeConfigurationFingerprint.Create(authoritativeConfiguration,
                    active.AzureDevOpsCredential.Revision, active.AiCredential.Revision),
                StringComparison.Ordinal);
        var persistedProgress = await progress.GetAsync(cancellationToken);
        return new SetupReadiness(
            infrastructureReady,
            admin,
            profile,
            draft is not null,
            profile || profileDetailsComplete,
            profile || policyDetailsComplete,
            ado.Configured,
            adoVerified,
            queryConfirmed,
            aiConfigured,
            aiVerified,
            aiModelConfigured,
            requiredAi,
            runtimeActivationCurrent,
            admin && profile && ado.Configured && adoVerified && queryConfirmed && aiConfigured && aiVerified &&
            aiModelConfigured &&
            runtimeActivationCurrent,
            persistedProgress);
    }
}

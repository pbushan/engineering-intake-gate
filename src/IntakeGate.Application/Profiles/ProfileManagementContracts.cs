using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.Profiles;

public sealed record ProfileEditableConfiguration(
    string? ProfileVersion,
    Uri PolicyUrl,
    IntakeStateConfiguration IntakeState,
    int AiTimeoutSeconds,
    IReadOnlyList<ModelPricing> AiPricing,
    ScheduleConfiguration Schedule,
    ProcessingConfiguration Processing,
    AuditConfiguration Audit,
    IReadOnlyList<ExclusionRule> Exclusions,
    IntakePolicy Policy);

public sealed record ProfileConfigurationSnapshot(
    DeploymentConfiguration Configuration,
    string Revision);

public enum ProfilePersistenceStatus
{
    Committed,
    AlreadyExists,
    NotFound,
    Conflict,
    SetupIncomplete
}

public sealed record ProfilePersistenceResult(
    ProfilePersistenceStatus Status,
    ProfileConfigurationSnapshot? Snapshot = null,
    RuntimeConfigurationGeneration? Generation = null);

public interface IProfileManagementRepository
{
    Task<ProfileConfigurationSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task<ProfilePersistenceResult> CreateFromConfirmedSetupAsync(
        DeploymentConfiguration configuration,
        AzureDevOpsSetupSettings expectedAzureDevOps,
        AiConfigurationSettings expectedAi,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<ProfilePersistenceResult> FinalizeFromOnboardingDraftAsync(
        DeploymentConfiguration configuration,
        AzureDevOpsSetupSettings expectedAzureDevOps,
        AiConfigurationSettings expectedAi,
        int expectedDraftRevision,
        RuntimeCredentialBinding azureDevOpsCredential,
        RuntimeCredentialBinding aiCredential,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<ProfilePersistenceResult> ImportAsync(
        DeploymentConfiguration configuration,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<ProfilePersistenceResult> UpdateAsync(
        DeploymentConfiguration configuration,
        string expectedRevision,
        AuditActor actor,
        DateTimeOffset nowUtc,
        IReadOnlyList<string> changedFields,
        CancellationToken cancellationToken = default);
}

public enum ProfileManagementStatus
{
    Succeeded,
    NotFound,
    Conflict,
    SetupIncomplete,
    InvalidConfiguration,
    ProductionNotAuthorized,
    RuntimeChangeInProgress,
    IntegrationValidationRequired,
    ActivationFailed
}

public sealed record ProfileManagementResult(
    ProfileManagementStatus Status,
    ProfileConfigurationSnapshot? Snapshot = null);

/// <summary>
/// Owns the bounded singleton-profile transitions and activates a new immutable runtime generation
/// only after a complete configuration commit succeeds.
/// </summary>
public sealed class ProfileManagementService(
    IProfileManagementRepository repository,
    IAzureDevOpsSetupRepository azureDevOpsSetup,
    IAzureDevOpsConfigurationRepository azureDevOpsConfiguration,
    IAiConfigurationRepository aiConfiguration,
    ISecretStore secrets,
    IDeploymentConfigurationValidator validator,
    IControlPlaneAuditWriter audit,
    IClock clock,
    DeploymentConfigurationState runtime,
    IRuntimeConfigurationActivator activator)
{
    public const string ManagedPolicyPath = "managed-policy";
    public const string AzureDevOpsCredentialSlotReference = "INTAKE_GATE_SECRET_SLOT_AZURE_DEVOPS";
    public const string OpenAiCredentialSlotReference = "INTAKE_GATE_SECRET_SLOT_OPENAI";
    public const string AnthropicCredentialSlotReference = "INTAKE_GATE_SECRET_SLOT_ANTHROPIC";

    public Task<ProfileConfigurationSnapshot?> GetAsync(CancellationToken cancellationToken = default) =>
        repository.LoadAsync(cancellationToken);

    public async Task<ProfileManagementResult> CreateAsync(
        ProfileEditableConfiguration editable,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editable);
        if (await repository.LoadAsync(cancellationToken) is not null)
            return new(ProfileManagementStatus.Conflict);
        var ado = await azureDevOpsSetup.GetAsync(cancellationToken);
        var ai = await aiConfiguration.GetAsync(cancellationToken);
        if (ado?.SavedQueryId is null || string.IsNullOrWhiteSpace(ado.ConfigurationFingerprint) ||
            ado.QueryValidatedAtUtc is null ||
            ai.ProfileConfigured || !ai.ModelConfirmed || string.IsNullOrWhiteSpace(ai.Provider) ||
            string.IsNullOrWhiteSpace(ai.Model) || ai.CredentialUpdatedAtUtc is null ||
            ai.ModelValidatedAtUtc is null)
            return await repository.LoadAsync(cancellationToken) is null
                ? new(ProfileManagementStatus.SetupIncomplete)
                : new(ProfileManagementStatus.Conflict);

        var adoCredential = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var aiSlot = AiProviderNames.CredentialSlot(ai.Provider);
        var aiCredential = await secrets.GetMetadataAsync(aiSlot, cancellationToken);
        if (!adoCredential.Configured || adoCredential.VerificationStatus != CredentialVerificationStatus.Verified ||
            adoCredential.UpdatedAtUtc is null || adoCredential.UpdatedAtUtc > ado.QueryValidatedAtUtc ||
            !aiCredential.Configured || aiCredential.VerificationStatus != CredentialVerificationStatus.Verified ||
            aiCredential.UpdatedAtUtc != ai.CredentialUpdatedAtUtc)
            return new(ProfileManagementStatus.SetupIncomplete);

        if (editable.Processing.ExecutionMode == ExecutionMode.Live)
            return await ProductionRejectedAsync(actor, "create", cancellationToken);

        DeploymentConfiguration candidate;
        try
        {
            var profileId = Guid.NewGuid().ToString("D");
            candidate = validator.ValidateConfiguration(new DeploymentConfiguration(
                new DeploymentProfile(
                    new ProfileIdentity(profileId, editable.ProfileVersion),
                    new IntakePolicyReference(ManagedPolicyPath, editable.PolicyUrl),
                    editable.IntakeState,
                    new AzureDevOpsConfiguration(ado.OrganizationUrl, ado.Project, ado.SavedQueryId.Value,
                        new CredentialReference(AzureDevOpsCredentialSlotReference)),
                    new AiConfiguration(ai.Provider, ai.Model, new CredentialReference(
                        ai.Provider == AiProviderNames.OpenAi
                            ? OpenAiCredentialSlotReference
                            : AnthropicCredentialSlotReference))
                    {
                        TimeoutSeconds = editable.AiTimeoutSeconds,
                        Pricing = editable.AiPricing
                    },
                    editable.Schedule,
                    editable.Processing,
                    editable.Audit,
                    editable.Exclusions),
                editable.Policy,
                PolicyFingerprint.Create(editable.Policy)),
                "profile creation", "intake policy creation");
        }
        catch (ConfigurationValidationException)
        {
            return new(ProfileManagementStatus.InvalidConfiguration);
        }

        if (!runtime.TryBeginManagementChange())
            return new(ProfileManagementStatus.RuntimeChangeInProgress);
        try
        {
            var persisted = await repository.CreateFromConfirmedSetupAsync(
                candidate, ado, ai, actor, clock.UtcNow.ToUniversalTime(), cancellationToken);
            if (persisted.Status == ProfilePersistenceStatus.Committed)
            {
                var activation = await activator.ActivateAuthoritativeAsync(actor,
                    ["profile", "ado", "ai", "policy", "schedule", "processing"], cancellationToken);
                runtime.CompleteManagementChange(false);
                if (!activation.Succeeded) return new(ProfileManagementStatus.ActivationFailed, persisted.Snapshot);
            }
            else runtime.CompleteManagementChange(false);
            return Map(persisted);
        }
        catch
        {
            runtime.CompleteManagementChange(false);
            throw;
        }
    }

    public async Task<ProfileManagementResult> UpdateAsync(
        string? expectedRevision,
        ProfileEditableConfiguration editable,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision))
            return new(ProfileManagementStatus.InvalidConfiguration);
        var current = await repository.LoadAsync(cancellationToken);
        if (current is null) return new(ProfileManagementStatus.NotFound);
        if (editable.Processing.ExecutionMode == ExecutionMode.Live)
            return await ProductionRejectedAsync(actor, current.Configuration.Profile.Identity.Id, cancellationToken);

        // A credential replacement invalidates the validation that authorized the current
        // integration configuration. Reject the profile save before persistence so an edit
        // can never leave the authoritative profile ahead of its verified integration state.
        var adoState = await azureDevOpsConfiguration.GetStateAsync(cancellationToken);
        var adoCredential = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var aiState = await aiConfiguration.GetAsync(cancellationToken);
        var aiProvider = current.Configuration.Profile.Ai.Provider;
        var aiCredential = await secrets.GetMetadataAsync(AiProviderNames.CredentialSlot(aiProvider), cancellationToken);
        if (adoCredential.VerificationStatus != CredentialVerificationStatus.Verified ||
            adoCredential.UpdatedAtUtc is null || adoState?.QueryConfirmed != true ||
            adoState.QueryValidatedAtUtc is null || adoCredential.UpdatedAtUtc > adoState.QueryValidatedAtUtc ||
            aiCredential.VerificationStatus != CredentialVerificationStatus.Verified ||
            aiCredential.UpdatedAtUtc is null || aiState.CredentialUpdatedAtUtc != aiCredential.UpdatedAtUtc ||
            !aiState.ModelConfirmed ||
            !string.Equals(aiState.Provider, current.Configuration.Profile.Ai.Provider, StringComparison.Ordinal) ||
            !string.Equals(aiState.Model, current.Configuration.Profile.Ai.Model, StringComparison.Ordinal))
            return new(ProfileManagementStatus.IntegrationValidationRequired);

        DeploymentConfiguration candidate;
        try
        {
            var existing = current.Configuration;
            candidate = validator.ValidateConfiguration(existing with
            {
                Profile = existing.Profile with
                {
                    Identity = existing.Profile.Identity with { Version = editable.ProfileVersion },
                    IntakePolicy = existing.Profile.IntakePolicy with { Url = editable.PolicyUrl },
                    IntakeState = editable.IntakeState,
                    Ai = existing.Profile.Ai with
                    {
                        TimeoutSeconds = editable.AiTimeoutSeconds,
                        Pricing = editable.AiPricing
                    },
                    Schedule = editable.Schedule,
                    Processing = editable.Processing,
                    Audit = editable.Audit,
                    Exclusions = editable.Exclusions
                },
                Policy = editable.Policy,
                PolicyFingerprint = PolicyFingerprint.Create(editable.Policy)
            }, "profile update", "intake policy update");
        }
        catch (ConfigurationValidationException)
        {
            return new(ProfileManagementStatus.InvalidConfiguration);
        }

        if (!runtime.TryBeginManagementChange())
            return new(ProfileManagementStatus.RuntimeChangeInProgress);
        try
        {
            var persisted = await repository.UpdateAsync(candidate, expectedRevision.Trim(), actor,
                clock.UtcNow.ToUniversalTime(), ChangedFields(current.Configuration, candidate), cancellationToken);
            if (persisted.Status == ProfilePersistenceStatus.Committed)
            {
                var changedFields = ChangedFields(current.Configuration, candidate);
                var activation = await activator.ActivateAuthoritativeAsync(actor, changedFields, cancellationToken);
                runtime.CompleteManagementChange(false);
                if (!activation.Succeeded) return new(ProfileManagementStatus.ActivationFailed, persisted.Snapshot);
            }
            else runtime.CompleteManagementChange(false);
            return Map(persisted);
        }
        catch
        {
            runtime.CompleteManagementChange(false);
            throw;
        }
    }

    public async Task<ProfileManagementResult> ImportAsync(
        DeploymentConfiguration configuration,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        DeploymentConfiguration validated;
        try
        {
            validated = validator.ValidateConfiguration(configuration,
                "legacy profile import", "legacy policy import");
        }
        catch (ConfigurationValidationException)
        {
            return new(ProfileManagementStatus.InvalidConfiguration);
        }
        if (validated.Profile.Processing.ExecutionMode == ExecutionMode.Live)
            return await ProductionRejectedAsync(actor, validated.Profile.Identity.Id, cancellationToken);
        if (!runtime.TryBeginManagementChange())
            return new(ProfileManagementStatus.RuntimeChangeInProgress);
        try
        {
            var persisted = await repository.ImportAsync(validated, actor,
                clock.UtcNow.ToUniversalTime(), cancellationToken);
            if (persisted.Status == ProfilePersistenceStatus.Committed)
            {
                var activation = await activator.ActivateAuthoritativeAsync(actor,
                    ["legacyImport", "profile", "policy"], cancellationToken);
                runtime.CompleteManagementChange(false);
                if (!activation.Succeeded) return new(ProfileManagementStatus.ActivationFailed, persisted.Snapshot);
            }
            else runtime.CompleteManagementChange(false);
            return Map(persisted);
        }
        catch
        {
            runtime.CompleteManagementChange(false);
            throw;
        }
    }

    private async Task<ProfileManagementResult> ProductionRejectedAsync(
        AuditActor actor, string target, CancellationToken cancellationToken)
    {
        await audit.AppendAsync(new ControlPlaneAuditRecord(Guid.NewGuid(), clock.UtcNow.ToUniversalTime(), actor,
            "ProfileProductionModeRejected", "Profile", target, ["executionMode", "releasePosture"]),
            cancellationToken);
        return new(ProfileManagementStatus.ProductionNotAuthorized);
    }

    private static ProfileManagementResult Map(ProfilePersistenceResult result) => result.Status switch
    {
        ProfilePersistenceStatus.Committed => new(ProfileManagementStatus.Succeeded, result.Snapshot),
        ProfilePersistenceStatus.AlreadyExists => new(ProfileManagementStatus.Conflict),
        ProfilePersistenceStatus.NotFound => new(ProfileManagementStatus.NotFound),
        ProfilePersistenceStatus.SetupIncomplete => new(ProfileManagementStatus.SetupIncomplete),
        _ => new(ProfileManagementStatus.Conflict)
    };

    private static IReadOnlyList<string> ChangedFields(
        DeploymentConfiguration current, DeploymentConfiguration candidate)
    {
        var fields = new List<string>();
        if (!string.Equals(current.Profile.Identity.Version, candidate.Profile.Identity.Version, StringComparison.Ordinal))
            fields.Add("profileVersion");
        if (current.Profile.IntakePolicy.Url != candidate.Profile.IntakePolicy.Url) fields.Add("policyUrl");
        if (current.Profile.IntakeState != candidate.Profile.IntakeState) fields.Add("intakeState");
        if (current.Profile.Ai.TimeoutSeconds != candidate.Profile.Ai.TimeoutSeconds ||
            !current.Profile.Ai.Pricing.SequenceEqual(candidate.Profile.Ai.Pricing)) fields.Add("aiRuntime");
        if (current.Profile.Schedule != candidate.Profile.Schedule) fields.Add("schedule");
        if (current.Profile.Processing != candidate.Profile.Processing) fields.Add("processing");
        if (current.Profile.Audit != candidate.Profile.Audit) fields.Add("audit");
        if (!current.Profile.Exclusions.SequenceEqual(candidate.Profile.Exclusions)) fields.Add("exclusions");
        if (!string.Equals(current.PolicyFingerprint, candidate.PolicyFingerprint, StringComparison.Ordinal)) fields.Add("policy");
        return fields;
    }
}

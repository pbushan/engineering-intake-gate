using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class RuntimeConfigurationActivatorTests
{
    private static readonly DateTimeOffset Revision = DateTimeOffset.Parse("2026-09-12T12:00:00Z");

    [Fact]
    public async Task CFG_014_ActivationPersistenceFailureKeepsPreviousGenerationActive()
    {
        var configuration = Configuration();
        var prior = Generation(7, configuration with
        {
            Profile = configuration.Profile with
            {
                Schedule = configuration.Profile.Schedule with { Enabled = false }
            }
        });
        var state = new DeploymentConfigurationState(prior.Configuration);
        state.Activate(prior);
        var audit = new AuditWriter();
        var generations = new ThrowingGenerationRepository(prior);
        var activator = Create(configuration, state, generations, audit);

        var result = await activator.ActivateAuthoritativeAsync(AuditActor.System, ["schedule"]);

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeActivationFailure.PersistenceFailure, result.Failure);
        Assert.Equal(prior.GenerationId, state.ActiveGeneration!.GenerationId);
        Assert.True(state.ExecutionAvailable);
        Assert.False(state.RuntimeActivationCurrent);
        Assert.Contains(audit.Records, item =>
            item.Operation == "RuntimeConfigurationActivationFailedPersistenceFailure");
    }

    [Fact]
    public async Task CFG_014_REL_001_LiveActivationIsRejectedWithoutMovingPreviousPointer()
    {
        var dryRun = Configuration();
        var prior = Generation(4, dryRun);
        var state = new DeploymentConfigurationState(prior.Configuration);
        state.Activate(prior);
        var generations = new ThrowingGenerationRepository(prior);
        var activator = Create(dryRun with
        {
            Profile = dryRun.Profile with
            {
                Processing = dryRun.Profile.Processing with { ExecutionMode = ExecutionMode.Live }
            }
        }, state, generations, new AuditWriter());

        var result = await activator.ActivateAuthoritativeAsync(AuditActor.System, ["executionMode"]);

        Assert.Equal(RuntimeActivationFailure.ProductionNotAuthorized, result.Failure);
        Assert.Equal(prior.GenerationId, state.ActiveGeneration!.GenerationId);
        Assert.Equal(0, generations.ActivationAttempts);
    }

    private static RuntimeConfigurationActivator Create(
        DeploymentConfiguration authoritative,
        DeploymentConfigurationState state,
        ThrowingGenerationRepository generations,
        AuditWriter audit) => new(
            new ProfileRepository(authoritative),
            new AdoRepository(authoritative.Profile.Identity.Id),
            new AiRepository(authoritative),
            new SecretStore(),
            generations,
            audit,
            state,
            new Clock());

    private static RuntimeConfigurationGeneration Generation(long id, DeploymentConfiguration configuration) =>
        new(id, configuration.Profile.Identity.Id, configuration with { RuntimeGenerationId = id },
            RuntimeConfigurationFingerprint.Create(configuration, Revision, Revision),
            new RuntimeCredentialBinding(CredentialSlot.AzureDevOps, SecretSourceKind.LocallyEncrypted, Revision),
            new RuntimeCredentialBinding(CredentialSlot.OpenAi, SecretSourceKind.LocallyEncrypted, Revision),
            Revision);

    private static DeploymentConfiguration Configuration() => new(
        new DeploymentProfile(
            new ProfileIdentity("profile", "1"),
            new IntakePolicyReference("managed-policy", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/example"), "Project", Guid.NewGuid(),
                new CredentialReference("ADO_SLOT")),
            new AiConfiguration("openai", "model-N1", new CredentialReference("AI_SLOT")),
            new ScheduleConfiguration(true, "0 * * * * *", "UTC", TimeSpan.FromDays(1)),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0,
                new ContentLimits(1_000, 10, 500), new AttachmentLimits(2, 1_000, 2_000, 10)),
            new AuditConfiguration(90), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("criterion", "Criterion", "Description", CriterionApplicability.Required,
                new NotApplicablePolicy(false, false), "Guidance")]),
        "sha256:policy");

    private sealed class ProfileRepository(DeploymentConfiguration configuration) : ISingletonProfileRepository
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<DeploymentConfiguration?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DeploymentConfiguration?>(configuration);
        public Task CreateAsync(DeploymentConfiguration value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AdoRepository(string profileId) : IAzureDevOpsConfigurationRepository
    {
        public Task<AzureDevOpsConfigurationState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureDevOpsConfigurationState?>(new(profileId, 1, "fingerprint", true, Revision));
        public Task<AzureDevOpsConfigurationCommitResult> CommitValidatedAsync(DeploymentConfiguration candidate,
            string expectedCurrentFingerprint, string candidateFingerprint, AuditActor actor,
            DateTimeOffset validatedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AiRepository(DeploymentConfiguration configuration) : IAiConfigurationRepository
    {
        public Task<AiConfigurationSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConfigurationSettings(true, configuration.Profile.Identity.Id,
                configuration.Profile.Ai.Provider, configuration.Profile.Ai.Model, true, Revision, Revision, "fingerprint"));
        public Task<AiConfigurationCommitResult> CommitValidatedAsync(DeploymentConfiguration? candidateConfiguration,
            string provider, string model, string expectedCurrentFingerprint, DateTimeOffset credentialUpdatedAtUtc,
            AuditActor actor, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SecretStore : ISecretStore
    {
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CredentialMetadata> GetMetadataAsync(CredentialSlot slot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CredentialMetadata(slot, true, SecretSourceKind.LocallyEncrypted,
                Revision, Revision, CredentialVerificationStatus.Verified, Revision, null));
        public async Task<IReadOnlyList<CredentialMetadata>> ListMetadataAsync(CancellationToken cancellationToken = default) =>
            [await GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken)];
        public Task<SecretResolution> ResolveAsync(CredentialSlot slot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ReplaceLocalAsync(CredentialSlot slot, SecretValue replacement, AuditActor actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureEnvironmentReferenceAsync(CredentialSlot slot, string environmentVariableName,
            AuditActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetVerificationAsync(CredentialSlot slot, CredentialVerificationStatus status,
            DateTimeOffset? verifiedAtUtc, CredentialVerificationDiagnostic? safeDiagnostic, AuditActor actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TrySetVerificationIfCurrentAsync(CredentialSlot slot, DateTimeOffset expectedUpdatedAtUtc,
            CredentialVerificationStatus status, DateTimeOffset? verifiedAtUtc,
            CredentialVerificationDiagnostic? safeDiagnostic, AuditActor actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingGenerationRepository(RuntimeConfigurationGeneration active)
        : IRuntimeConfigurationGenerationRepository
    {
        public int ActivationAttempts { get; private set; }
        public Task<RuntimeConfigurationGeneration?> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<RuntimeConfigurationGeneration?>(active);
        public Task<RuntimeConfigurationGeneration?> GetAsync(long generationId,
            CancellationToken cancellationToken = default) => Task.FromResult<RuntimeConfigurationGeneration?>(active);
        public Task<RuntimeConfigurationGeneration> ActivateAsync(DeploymentConfiguration configuration,
            string configurationFingerprint, RuntimeCredentialBinding azureDevOpsCredential,
            RuntimeCredentialBinding aiCredential, AuditActor actor, DateTimeOffset nowUtc,
            IReadOnlyList<string> changedFields, CancellationToken cancellationToken = default)
        {
            ActivationAttempts++;
            throw new InvalidOperationException("synthetic persistence failure");
        }
    }

    private sealed class AuditWriter : IControlPlaneAuditWriter
    {
        public List<ControlPlaneAuditRecord> Records { get; } = [];
        public Task AppendAsync(ControlPlaneAuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Revision;
    }
}

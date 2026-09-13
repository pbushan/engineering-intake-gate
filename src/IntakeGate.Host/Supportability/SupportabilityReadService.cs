using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Setup;
using IntakeGate.Host.Discovery;
using IntakeGate.Host.Operations;
using IntakeGate.Infrastructure.Persistence;

namespace IntakeGate.Host.Supportability;

public sealed class SupportabilityReadService(
    IApplicationRuntimeRepository runtimeRepository,
    SetupStateService setupState,
    DeploymentConfigurationState configurationState,
    ISecretStore secrets,
    IAiConfigurationRepository aiConfigurations,
    IIncrementalScheduleCalculator scheduleCalculator,
    OperationalRunReadService operationalRuns,
    IControlPlaneAuditRepository controlPlaneAudits,
    IHostEnvironment environment)
{
    private const int RecentFailureLimit = 10;

    public async Task<SystemHealthResponse> GetHealthAsync(
        bool includeAdminDiagnostics,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var databaseReachable = await runtimeRepository.CanAccessAsync(cancellationToken);
        var setup = await setupState.GetAsync(cancellationToken);
        var generation = configurationState.ActiveGeneration;
        var authoritative = configurationState.Configuration;
        var adoCredential = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var aiSettings = await aiConfigurations.GetAsync(cancellationToken);
        var aiProvider = aiSettings.Provider ?? authoritative?.Profile.Ai.Provider;
        var aiCredential = AiProviderNames.TryNormalize(aiProvider, out var normalizedProvider)
            ? await secrets.GetMetadataAsync(AiProviderNames.CredentialSlot(normalizedProvider), cancellationToken)
            : null;

        var runtimeStatus = generation is null
            ? RuntimeHealthStatus.NoActiveGeneration
            : configurationState.RuntimeActivationCurrent
                ? RuntimeHealthStatus.Active
                : RuntimeHealthStatus.ActivationFailed;

        var scheduler = Scheduler(generation, runtimeStatus, now);
        var failures = await RecentFailuresAsync(cancellationToken);
        return new SystemHealthResponse(
            now,
            new ApplicationHealthResponse(InfrastructureHealthStatus.Healthy, ProductMetadata.ApplicationName,
                ProductMetadata.Version, environment.EnvironmentName),
            new DatabaseHealthResponse(
                databaseReachable ? InfrastructureHealthStatus.Healthy : InfrastructureHealthStatus.Unavailable,
                databaseReachable,
                SqliteDatabaseMigrator.CurrentSchemaVersion,
                databaseReachable),
            new SetupHealthResponse(setup.SetupComplete ? SetupHealthStatus.Ready : SetupHealthStatus.Incomplete,
                setup.SetupComplete, setup.ProfileConfigured, setup.AzureDevOpsSavedQueryConfirmed),
            new RuntimeHealthResponse(runtimeStatus, generation?.GenerationId,
                configurationState.RuntimeActivationCurrent),
            Integration(adoCredential,
                setup.AzureDevOpsSavedQueryConfirmed, null, null),
            Integration(aiCredential,
                setup.AiModelConfigured, aiProvider, aiSettings.Model ?? authoritative?.Profile.Ai.Model),
            scheduler,
            failures,
            includeAdminDiagnostics
                ? new AiDiagnosticMetadataResponse(
                    aiCredential?.UpdatedAtUtc ?? aiCredential?.CreatedAtUtc,
                    authoritative?.Profile.Ai.Provider,
                    authoritative?.Profile.Ai.Model)
                : null);
    }

    public async Task<ControlPlaneAuditPageResponse> GetAuditAsync(
        ControlPlaneAuditQuery query,
        CancellationToken cancellationToken)
    {
        var page = await controlPlaneAudits.ListControlPlaneAsync(query, cancellationToken);
        return new ControlPlaneAuditPageResponse(page.Page, page.PageSize, page.TotalCount,
            page.TotalCount == 0 ? 0 : (int)Math.Ceiling(page.TotalCount / (double)page.PageSize),
            page.Records.Select(SafeAudit).ToArray());
    }

    private SchedulerHealthResponse Scheduler(
        RuntimeConfigurationGeneration? generation,
        RuntimeHealthStatus runtimeStatus,
        DateTimeOffset now)
    {
        if (generation is null)
            return new SchedulerHealthResponse(SchedulerHealthStatus.Waiting, false, null, null, null);
        var schedule = generation.Configuration.Profile.Schedule;
        if (runtimeStatus == RuntimeHealthStatus.ActivationFailed)
            return new SchedulerHealthResponse(SchedulerHealthStatus.ActivationFailed,
                schedule.Enabled, schedule.Timezone, null, generation.GenerationId);
        if (!schedule.Enabled)
            return new SchedulerHealthResponse(SchedulerHealthStatus.ManualOnly,
                false, schedule.Timezone, null, generation.GenerationId);
        try
        {
            return new SchedulerHealthResponse(SchedulerHealthStatus.Scheduled, true, schedule.Timezone,
                scheduleCalculator.GetNextOccurrenceUtc(schedule, now), generation.GenerationId);
        }
        catch
        {
            return new SchedulerHealthResponse(SchedulerHealthStatus.ActivationFailed,
                true, schedule.Timezone, null, generation.GenerationId);
        }
    }

    private async Task<IReadOnlyList<RecentFailureResponse>> RecentFailuresAsync(CancellationToken cancellationToken)
    {
        var queries = new[] { OperationalRunStatus.Error, OperationalRunStatus.CompletedWithErrors };
        var pages = new List<RunHistoryPageResponse>(queries.Length);
        foreach (var status in queries)
            pages.Add(await operationalRuns.ListAsync(new OperationalRunQuery(
                1, RecentFailureLimit, null, null, null, status, null), cancellationToken));

        return pages.SelectMany(page => page.Items)
            .OrderByDescending(item => item.StartedAtUtc)
            .ThenByDescending(item => item.RunId)
            .Take(RecentFailureLimit)
            .Select(item =>
            {
                var error = item.Errors.FirstOrDefault();
                return new RecentFailureResponse(item.RunId, item.StartedAtUtc, item.Status,
                    error?.Category ?? item.Status.ToString(),
                    error?.Message ?? "The run did not complete successfully.");
            })
            .ToArray();
    }

    private static IntegrationHealthResponse Integration(
        CredentialMetadata? metadata,
        bool settingsReady,
        string? provider,
        string? model)
    {
        metadata ??= new CredentialMetadata(CredentialSlot.OpenAi, false, null, null, null,
            CredentialVerificationStatus.NeverVerified, null, null);
        return new IntegrationHealthResponse(Connection(metadata), metadata.Configured,
            metadata.VerificationStatus, metadata.LastVerifiedAtUtc, metadata.VerificationDiagnostic,
            settingsReady, provider, model);
    }

    private static ConnectionHealthStatus Connection(CredentialMetadata metadata)
    {
        if (!metadata.Configured) return ConnectionHealthStatus.CredentialMissing;
        if (metadata.VerificationStatus == CredentialVerificationStatus.Verified)
            return ConnectionHealthStatus.Verified;
        return metadata.VerificationDiagnostic switch
        {
            CredentialVerificationDiagnostic.AuthenticationRejected => ConnectionHealthStatus.AuthenticationFailed,
            CredentialVerificationDiagnostic.PermissionDenied => ConnectionHealthStatus.AuthorizationFailed,
            CredentialVerificationDiagnostic.RateLimited => ConnectionHealthStatus.RateLimited,
            CredentialVerificationDiagnostic.Timeout => ConnectionHealthStatus.Timeout,
            CredentialVerificationDiagnostic.ProviderUnavailable => ConnectionHealthStatus.ProviderUnavailable,
            _ => ConnectionHealthStatus.NotVerified
        };
    }

    private static ControlPlaneAuditItemResponse SafeAudit(ControlPlaneAuditRecord record) => new(
        record.Id,
        record.OccurredAtUtc,
        record.Actor.Id == Guid.Empty
            ? new AuditActorResponse(AuditActorDisplayType.System, null, "System")
            : string.IsNullOrWhiteSpace(record.Actor.Username)
                ? new AuditActorResponse(AuditActorDisplayType.Unknown, record.Actor.Id, "Unknown")
                : new AuditActorResponse(AuditActorDisplayType.User, record.Actor.Id, record.Actor.Username),
        record.Operation,
        record.TargetCategory,
        record.TargetId,
        record.ChangedFields.Select(SafeChangedField).Where(field => field is not null)
            .Cast<string>().Distinct(StringComparer.Ordinal).ToArray());

    private static string? SafeChangedField(string field) => field switch
    {
        "passwordHash" or "password" => "authenticationCredential",
        "credential" => "credentialConfiguration",
        "environmentReference" => "credentialSource",
        var value when value.Contains("ciphertext", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("csrf", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("providerBody", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("completion", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("evidence", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("attachmentContent", StringComparison.OrdinalIgnoreCase) => null,
        _ => field
    };
}

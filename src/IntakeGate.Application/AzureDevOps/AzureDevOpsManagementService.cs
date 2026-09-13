using System.Collections.Concurrent;
using System.Security.Cryptography;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.AzureDevOps;

public sealed record AzureDevOpsCandidatePreview(
    string ConfirmationToken,
    DateTimeOffset ExpiresAtUtc,
    string OrganizationUrl,
    string Project,
    Guid SavedQueryId,
    string CandidateFingerprint,
    int TotalCount,
    IReadOnlyList<AzureDevOpsQueryPreviewItem> Preview);

public enum AzureDevOpsCandidateFailure
{
    ProfileNotConfigured,
    InvalidSettings,
    InvalidSavedQueryInput,
    CredentialUnavailable,
    ProviderFailure,
    CandidateStale,
    ConfirmationConflict,
    ActiveRunInProgress,
    ActivationFailed
}

public sealed record AzureDevOpsCandidateResult(
    AzureDevOpsCandidatePreview? Candidate,
    AzureDevOpsCandidateFailure? Failure,
    AzureDevOpsManagementFailure? ProviderFailure = null);

public sealed record AzureDevOpsConfirmationResult(
    bool Succeeded,
    int? Generation,
    bool RestartRequired,
    AzureDevOpsCandidateFailure? Failure = null);

public sealed class AzureDevOpsManagementService(
    ISingletonProfileRepository profiles,
    ISecretStore secrets,
    IAzureDevOpsManagementClientFactory clients,
    IAzureDevOpsConfigurationRepository configurations,
    IDeploymentConfigurationValidator validator,
    IControlPlaneAuditWriter audit,
    IClock clock,
    DeploymentConfigurationState runtimeConfiguration,
    IRuntimeConfigurationActivator activator,
    ISecretRedactor secretRedactor,
    IAzureDevOpsSetupRepository setupSettings)
{
    private static readonly TimeSpan CandidateLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, ValidatedCandidate> candidates = new(StringComparer.Ordinal);

    public async Task<string?> SaveProfilelessSettingsAsync(
        string? organizationUrl,
        string? project,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        if (await profiles.ExistsAsync(cancellationToken)) return "ValidatedCandidateRequired";
        if (!AzureDevOpsSettingsValidation.TryNormalizeOrganization(organizationUrl, out var organization) ||
            !AzureDevOpsSettingsValidation.TryNormalizeProject(project, out var normalizedProject))
            return "InvalidAzureDevOpsSettings";
        await setupSettings.SaveAsync(organization!, normalizedProject, actor,
            clock.UtcNow.ToUniversalTime(), cancellationToken);
        return null;
    }

    public async Task<AzureDevOpsConnectionResult> TestConnectionAsync(
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow.ToUniversalTime();
        await audit.AppendAsync(NewAudit(actor, now, "AzureDevOpsConnectionVerificationAttempted",
            "connection", ["credentialSource", "organizationUrl", "project"]), cancellationToken);
        var configuration = await profiles.LoadAsync(cancellationToken);
        AzureDevOpsConfiguration ado;
        var retries = 2;
        if (configuration is null)
        {
            var staged = await setupSettings.GetAsync(cancellationToken);
            if (staged is null)
                return await RecordConnectionFailureAsync(actor,
                    AzureDevOpsManagementFailure.OrganizationOrProjectUnavailable, cancellationToken);
            ado = new AzureDevOpsConfiguration(staged.OrganizationUrl, staged.Project, Guid.Empty,
                new CredentialReference("MANAGEMENT_SECRET_STORE"));
        }
        else
        {
            ado = configuration.Profile.Ado;
            retries = configuration.Profile.Processing.Retries;
        }

        var secret = await secrets.ResolveAsync(CredentialSlot.AzureDevOps, cancellationToken);
        if (secret.Availability != SecretAvailability.Available)
            return await RecordConnectionFailureAsync(actor, AzureDevOpsManagementFailure.CredentialUnavailable, cancellationToken);

        AzureDevOpsConnectionResult result;
        try
        {
            result = await clients.Create(ado, secret.Secret!, retries).TestConnectionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            result = new AzureDevOpsConnectionResult(false, AzureDevOpsManagementFailure.UnexpectedFailure);
        }

        if (result.Succeeded)
        {
            var verifiedAt = clock.UtcNow.ToUniversalTime();
            await secrets.SetVerificationAsync(CredentialSlot.AzureDevOps, CredentialVerificationStatus.Verified,
                verifiedAt, null, actor, cancellationToken);
            await audit.AppendAsync(NewAudit(actor, verifiedAt, "AzureDevOpsConnectionVerificationSucceeded",
                "connection", ["verificationStatus"]), cancellationToken);
            return result;
        }

        return await RecordConnectionFailureAsync(actor,
            result.Failure ?? AzureDevOpsManagementFailure.UnexpectedFailure, cancellationToken);
    }

    public async Task<AzureDevOpsCandidateResult> ValidateCandidateAsync(
        string? organizationUrl,
        string? project,
        string? savedQuery,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var current = await profiles.LoadAsync(cancellationToken);
        var staged = current is null ? await setupSettings.GetAsync(cancellationToken) : null;
        if (current is null && staged is null)
            return new(null, AzureDevOpsCandidateFailure.InvalidSettings);
        var organizationValue = string.IsNullOrWhiteSpace(organizationUrl)
            ? current?.Profile.Ado.OrganizationUrl.AbsoluteUri ?? staged!.OrganizationUrl.AbsoluteUri
            : organizationUrl.Trim();
        var projectValue = string.IsNullOrWhiteSpace(project)
            ? current?.Profile.Ado.Project ?? staged!.Project
            : project.Trim();
        if (!AzureDevOpsSettingsValidation.TryNormalizeOrganization(organizationValue, out var normalizedOrganization) ||
            !AzureDevOpsSettingsValidation.TryNormalizeProject(projectValue, out projectValue))
            return new(null, AzureDevOpsCandidateFailure.InvalidSettings);
        if (current is not null &&
            (!string.Equals(normalizedOrganization!.AbsoluteUri,
                 current.Profile.Ado.OrganizationUrl.AbsoluteUri, StringComparison.Ordinal) ||
             !string.Equals(projectValue, current.Profile.Ado.Project, StringComparison.Ordinal)))
            return new(null, AzureDevOpsCandidateFailure.InvalidSettings);
        if (!AzureDevOpsSavedQueryResolver.TryResolve(savedQuery, normalizedOrganization!, projectValue, out var queryId))
            return new(null, AzureDevOpsCandidateFailure.InvalidSavedQueryInput);

        var baseAdo = current?.Profile.Ado ?? new AzureDevOpsConfiguration(
            staged!.OrganizationUrl, staged.Project, staged.SavedQueryId ?? Guid.Empty,
            new CredentialReference("MANAGEMENT_SECRET_STORE"));
        var candidateAdo = baseAdo with
        {
            OrganizationUrl = normalizedOrganization!,
            Project = projectValue,
            SavedQueryId = queryId
        };
        DeploymentConfiguration? candidate = null;
        if (current is not null)
        {
            try
            {
                candidate = validator.ValidateConfiguration(current with
                {
                    Profile = current.Profile with { Ado = candidateAdo }
                }, "Azure DevOps query candidate", "persisted SQLite policy");
                candidateAdo = candidate.Profile.Ado;
            }
            catch (ConfigurationValidationException)
            {
                return new(null, AzureDevOpsCandidateFailure.InvalidSettings);
            }
        }

        var metadata = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var secret = await secrets.ResolveAsync(CredentialSlot.AzureDevOps, cancellationToken);
        if (secret.Availability != SecretAvailability.Available)
            return new(null, AzureDevOpsCandidateFailure.CredentialUnavailable);
        AzureDevOpsQueryValidationResult validation;
        try
        {
            validation = await clients.Create(candidateAdo, secret.Secret!,
                    candidate?.Profile.Processing.Retries ?? 2)
                .ValidateSavedQueryAsync(queryId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            validation = AzureDevOpsQueryValidationResult.Failed(
                queryId, AzureDevOpsManagementFailure.UnexpectedFailure);
        }
        if (!validation.Succeeded)
            return new(null, AzureDevOpsCandidateFailure.ProviderFailure, validation.Failure);

        var fingerprintProfileId = current?.Profile.Identity.Id ?? "profileless";
        var currentAdo = current?.Profile.Ado ?? baseAdo;
        var currentFingerprint = AzureDevOpsConfigurationFingerprint.Create(fingerprintProfileId, currentAdo);
        var candidateFingerprint = AzureDevOpsConfigurationFingerprint.Create(
            fingerprintProfileId, candidateAdo);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.ToUniversalTime() + CandidateLifetime;
        candidates[token] = new ValidatedCandidate(candidate, candidateAdo, currentFingerprint,
            candidateFingerprint, metadata.UpdatedAtUtc, actor.Id, expires);
        PruneExpired(clock.UtcNow.ToUniversalTime());
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow.ToUniversalTime(),
            "AzureDevOpsSavedQueryCandidateValidated", candidateFingerprint,
            ["organizationUrl", "project", "savedQueryId", "totalCount", "preview"]), cancellationToken);
        var safePreview = validation.Preview.Select(item => item with
        {
            Title = BoundedRedact(item.Title, 512),
            WorkItemType = BoundedRedact(item.WorkItemType, 128),
            State = BoundedRedact(item.State, 128)
        }).ToArray();
        return new(new AzureDevOpsCandidatePreview(token, expires,
            candidateAdo.OrganizationUrl.AbsoluteUri, candidateAdo.Project,
            queryId, candidateFingerprint, validation.TotalCount, safePreview), null);
    }

    public async Task<AzureDevOpsConfirmationResult> ConfirmAsync(
        string? confirmationToken,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(confirmationToken) ||
            !candidates.TryRemove(confirmationToken.Trim(), out var candidate))
            return new(false, null, false, AzureDevOpsCandidateFailure.ConfirmationConflict);
        if (candidate.ExpiresAtUtc <= clock.UtcNow.ToUniversalTime())
            return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
        if (candidate.ValidatedByUserId != actor.Id)
            return new(false, null, false, AzureDevOpsCandidateFailure.ConfirmationConflict);

        var metadata = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        if (metadata.UpdatedAtUtc != candidate.CredentialUpdatedAtUtc)
            return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
        var current = await profiles.LoadAsync(cancellationToken);
        if (candidate.Configuration is null)
        {
            if (current is not null)
                return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
            var staged = await setupSettings.GetAsync(cancellationToken);
            if (staged is null) return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
            var currentAdo = candidate.AzureDevOps with
            {
                OrganizationUrl = staged.OrganizationUrl,
                Project = staged.Project,
                SavedQueryId = staged.SavedQueryId ?? Guid.Empty
            };
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(candidate.ExpectedCurrentFingerprint),
                    System.Text.Encoding.UTF8.GetBytes(AzureDevOpsConfigurationFingerprint.Create(
                        "profileless", currentAdo))))
                return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
            var stagedResult = await setupSettings.ConfirmQueryAsync(candidate.AzureDevOps,
                candidate.ExpectedCurrentFingerprint, candidate.CandidateFingerprint, actor,
                clock.UtcNow.ToUniversalTime(), cancellationToken);
            return stagedResult
                ? new AzureDevOpsConfirmationResult(true, 0, false)
                : new AzureDevOpsConfirmationResult(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);
        }
        if (current is null) return new(false, null, false, AzureDevOpsCandidateFailure.ProfileNotConfigured);
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(candidate.ExpectedCurrentFingerprint),
                System.Text.Encoding.UTF8.GetBytes(AzureDevOpsConfigurationFingerprint.Create(
                    current.Profile.Identity.Id, current.Profile.Ado))))
            return new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale);

        if (!runtimeConfiguration.TryBeginManagementChange())
            return new(false, null, false, AzureDevOpsCandidateFailure.ActiveRunInProgress);
        AzureDevOpsConfigurationCommitResult committed;
        try
        {
            committed = await configurations.CommitValidatedAsync(candidate.Configuration,
                candidate.ExpectedCurrentFingerprint, candidate.CandidateFingerprint, actor,
                clock.UtcNow.ToUniversalTime(), cancellationToken);
            if (committed.Status is AzureDevOpsConfigurationCommitStatus.Committed or AzureDevOpsConfigurationCommitStatus.AlreadyCurrent)
            {
                var activation = await activator.ActivateAuthoritativeAsync(actor,
                    ["ado.organizationUrl", "ado.project", "ado.savedQueryId", "queryGeneration"], cancellationToken);
                runtimeConfiguration.CompleteManagementChange(false);
                if (!activation.Succeeded)
                    return new(false, committed.Generation, false, AzureDevOpsCandidateFailure.ActivationFailed);
            }
            else runtimeConfiguration.CompleteManagementChange(false);
        }
        catch
        {
            runtimeConfiguration.CompleteManagementChange(false);
            throw;
        }
        return committed.Status switch
        {
            AzureDevOpsConfigurationCommitStatus.Committed or AzureDevOpsConfigurationCommitStatus.AlreadyCurrent =>
                new(true, committed.Generation, false),
            AzureDevOpsConfigurationCommitStatus.ActiveRunInProgress =>
                new(false, null, false, AzureDevOpsCandidateFailure.ActiveRunInProgress),
            AzureDevOpsConfigurationCommitStatus.ProfileNotConfigured =>
                new(false, null, false, AzureDevOpsCandidateFailure.ProfileNotConfigured),
            _ => new(false, null, false, AzureDevOpsCandidateFailure.CandidateStale)
        };
    }

    private async Task<AzureDevOpsConnectionResult> RecordConnectionFailureAsync(
        AuditActor actor,
        AzureDevOpsManagementFailure failure,
        CancellationToken cancellationToken)
    {
        var diagnostic = failure switch
        {
            AzureDevOpsManagementFailure.AuthenticationFailed => CredentialVerificationDiagnostic.AuthenticationRejected,
            AzureDevOpsManagementFailure.AuthorizationFailed => CredentialVerificationDiagnostic.PermissionDenied,
            AzureDevOpsManagementFailure.Timeout => CredentialVerificationDiagnostic.Timeout,
            AzureDevOpsManagementFailure.ProviderUnavailable => CredentialVerificationDiagnostic.ProviderUnavailable,
            _ => CredentialVerificationDiagnostic.Unknown
        };
        var metadata = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        if (metadata.Configured && failure != AzureDevOpsManagementFailure.ProfileNotConfigured)
            await secrets.SetVerificationAsync(CredentialSlot.AzureDevOps, CredentialVerificationStatus.Failed,
                null, diagnostic, actor, cancellationToken);
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow.ToUniversalTime(),
            "AzureDevOpsConnectionVerificationFailed", failure.ToString(), ["verificationStatus", "safeDiagnostic"]), cancellationToken);
        return new AzureDevOpsConnectionResult(false, failure);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in candidates)
            if (entry.Value.ExpiresAtUtc <= now) candidates.TryRemove(entry.Key, out _);
    }

    private string BoundedRedact(string value, int maximumLength)
    {
        var bounded = value.Length <= maximumLength ? value : value[..maximumLength];
        return secretRedactor.Redact(bounded).Content;
    }

    private static ControlPlaneAuditRecord NewAudit(
        AuditActor actor, DateTimeOffset at, string operation, string target,
        IReadOnlyList<string> fields) => new(Guid.NewGuid(), at, actor, operation,
        "AzureDevOps", target, fields);

    private sealed record ValidatedCandidate(
        DeploymentConfiguration? Configuration,
        AzureDevOpsConfiguration AzureDevOps,
        string ExpectedCurrentFingerprint,
        string CandidateFingerprint,
        DateTimeOffset? CredentialUpdatedAtUtc,
        Guid ValidatedByUserId,
        DateTimeOffset ExpiresAtUtc);
}

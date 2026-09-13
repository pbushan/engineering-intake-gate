using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.AiManagement;

public enum AiCandidateFailure
{
    InvalidProvider,
    InvalidModelIdentifier,
    CredentialUnavailable,
    CredentialNotVerified,
    ProviderFailure,
    CandidateStale,
    ConfirmationConflict,
    ActivationFailed
}

public sealed record AiValidatedCandidate(
    string ConfirmationToken,
    DateTimeOffset ExpiresAtUtc,
    string Provider,
    string Model,
    string? DisplayName);

public sealed record AiCandidateResult(
    AiValidatedCandidate? Candidate,
    AiCandidateFailure? Failure,
    AiManagementFailure? ProviderFailure = null);

public sealed record AiConfirmationResult(
    bool Succeeded,
    bool ProfileConfigured,
    bool RestartRequired,
    AiCandidateFailure? Failure = null);

public sealed class AiManagementService(
    ISingletonProfileRepository profiles,
    ISecretStore secrets,
    IAiManagementClientFactory clients,
    IAiConfigurationRepository configurations,
    IDeploymentConfigurationValidator validator,
    IControlPlaneAuditWriter audit,
    IClock clock,
    DeploymentConfigurationState runtimeConfiguration,
    IRuntimeConfigurationActivator activator,
    ISecretRedactor redactor)
{
    private static readonly TimeSpan CandidateLifetime = TimeSpan.FromMinutes(10);
    private static readonly Regex ModelIdentifier = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ConcurrentDictionary<string, Candidate> candidates = new(StringComparer.Ordinal);

    public async Task ReplaceLocalCredentialAsync(
        string provider, SecretValue replacement, AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProvider(provider);
        await secrets.ReplaceLocalAsync(AiProviderNames.CredentialSlot(normalized), replacement, actor, cancellationToken);
        runtimeConfiguration.MarkActivationPending();
        InvalidateProviderCandidates(normalized);
    }

    public async Task ReplaceEnvironmentCredentialAsync(
        string provider, string environmentVariableName, AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProvider(provider);
        await secrets.ConfigureEnvironmentReferenceAsync(AiProviderNames.CredentialSlot(normalized),
            environmentVariableName, actor, cancellationToken);
        runtimeConfiguration.MarkActivationPending();
        InvalidateProviderCandidates(normalized);
    }

    public async Task<AiCredentialVerificationResult> VerifyCredentialAsync(
        string provider, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (!AiProviderNames.TryNormalize(provider, out var normalized))
            return new(false, AiManagementFailure.UnexpectedFailure);
        var slot = AiProviderNames.CredentialSlot(normalized);
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow, "AiCredentialVerificationAttempted",
            normalized, ["provider", "credentialSource"]), cancellationToken);
        var credentialMetadata = await secrets.GetMetadataAsync(slot, cancellationToken);
        var resolution = await secrets.ResolveAsync(slot, cancellationToken);
        if (resolution.Availability != SecretAvailability.Available)
            return await RecordVerificationFailureAsync(normalized, slot, AiManagementFailure.CredentialUnavailable,
                actor, credentialMetadata.UpdatedAtUtc, cancellationToken);

        AiCredentialVerificationResult result;
        try
        {
            result = await clients.Create(normalized, resolution.Secret!, TimeSpan.FromSeconds(30))
                .VerifyCredentialAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { result = new(false, AiManagementFailure.UnexpectedFailure); }

        if (result.Succeeded)
        {
            var now = clock.UtcNow.ToUniversalTime();
            if (credentialMetadata.UpdatedAtUtc is null ||
                !await secrets.TrySetVerificationIfCurrentAsync(slot, credentialMetadata.UpdatedAtUtc.Value,
                    CredentialVerificationStatus.Verified, now, null, actor, cancellationToken))
                return await RecordStaleCredentialAsync(normalized, actor, cancellationToken);
            await audit.AppendAsync(NewAudit(actor, now, "AiCredentialVerificationSucceeded",
                normalized, ["provider", "verificationStatus"]), cancellationToken);
            return result;
        }
        return await RecordVerificationFailureAsync(normalized, slot,
            result.Failure ?? AiManagementFailure.UnexpectedFailure, actor,
            credentialMetadata.UpdatedAtUtc, cancellationToken);
    }

    public async Task<AiModelDiscoveryResult> DiscoverModelsAsync(
        string provider, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (!AiProviderNames.TryNormalize(provider, out var normalized))
            return new(false, [], AiManagementFailure.UnexpectedFailure);
        var prepared = await PrepareClientAsync(normalized, cancellationToken);
        if (prepared.Failure is { } failure)
            return new(false, [], failure);
        AiModelDiscoveryResult result;
        try { result = await prepared.Client!.DiscoverModelsAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { result = new(false, [], AiManagementFailure.UnexpectedFailure); }
        if (!await CredentialStillCurrentAsync(normalized, prepared.CredentialUpdatedAtUtc, cancellationToken))
            result = new(false, [], AiManagementFailure.CredentialChanged);
        var safeModels = result.Succeeded
            ? result.Models.Where(model => IsValidModelId(model.Id) && model.Provider == normalized)
                .Select(model => model with { DisplayName = SafeLabel(model.DisplayName) })
                .DistinctBy(model => model.Id, StringComparer.Ordinal)
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .Take(500).ToArray()
            : [];
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow,
            result.Succeeded ? "AiModelDiscoverySucceeded" : "AiModelDiscoveryFailed",
            normalized, ["provider", "resultClassification", "modelCount"]), cancellationToken);
        return result.Succeeded ? new(true, safeModels) : new(false, [], result.Failure);
    }

    public async Task<AiCandidateResult> ValidateCandidateAsync(
        string? provider, string? model, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (!AiProviderNames.TryNormalize(provider, out var normalized))
            return new(null, AiCandidateFailure.InvalidProvider);
        var normalizedModel = model?.Trim() ?? string.Empty;
        if (!IsValidModelId(normalizedModel))
            return new(null, AiCandidateFailure.InvalidModelIdentifier);
        var prepared = await PrepareClientAsync(normalized, cancellationToken);
        if (prepared.Failure is AiManagementFailure.CredentialUnavailable)
            return new(null, AiCandidateFailure.CredentialUnavailable);
        if (prepared.Failure is AiManagementFailure.CredentialNotVerified)
            return new(null, AiCandidateFailure.CredentialNotVerified);

        AiModelValidationResult validation;
        try { validation = await prepared.Client!.ValidateModelAsync(normalizedModel, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { validation = new(false, null, AiManagementFailure.UnexpectedFailure); }
        if (!await CredentialStillCurrentAsync(normalized, prepared.CredentialUpdatedAtUtc, cancellationToken))
            return new(null, AiCandidateFailure.CandidateStale);
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow,
            validation.Succeeded ? "AiModelCandidateValidated" : "AiModelCandidateValidationFailed",
            $"{normalized}:{normalizedModel}", ["provider", "model", "resultClassification"]), cancellationToken);
        if (!validation.Succeeded)
            return new(null, AiCandidateFailure.ProviderFailure, validation.Failure);
        if (validation.Model is not { } validated || validated.Provider != normalized ||
            !IsValidModelId(validated.Id))
            return new(null, AiCandidateFailure.ProviderFailure, AiManagementFailure.InvalidProviderResponse);

        var state = await configurations.GetAsync(cancellationToken);
        if (prepared.CredentialUpdatedAtUtc is null)
            return new(null, AiCandidateFailure.CredentialUnavailable);
        InvalidateActorCandidates(actor.Id);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.ToUniversalTime() + CandidateLifetime;
        candidates[token] = new Candidate(normalized, normalizedModel, SafeLabel(validated.DisplayName),
            state.Fingerprint, prepared.CredentialUpdatedAtUtc.Value, actor.Id, expires);
        PruneExpired(clock.UtcNow.ToUniversalTime());
        return new(new AiValidatedCandidate(token, expires, normalized, normalizedModel,
            SafeLabel(validated.DisplayName)), null);
    }

    public async Task<AiConfirmationResult> ConfirmAsync(
        string? confirmationToken, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(confirmationToken) ||
            !candidates.TryRemove(confirmationToken.Trim(), out var candidate))
            return new(false, false, false, AiCandidateFailure.ConfirmationConflict);
        if (candidate.ExpiresAtUtc <= clock.UtcNow.ToUniversalTime())
            return new(false, false, false, AiCandidateFailure.CandidateStale);
        if (candidate.ActorId != actor.Id)
            return new(false, false, false, AiCandidateFailure.ConfirmationConflict);
        var credential = await secrets.GetMetadataAsync(AiProviderNames.CredentialSlot(candidate.Provider), cancellationToken);
        if (credential.UpdatedAtUtc != candidate.CredentialUpdatedAtUtc ||
            credential.VerificationStatus != CredentialVerificationStatus.Verified)
            return new(false, false, false, AiCandidateFailure.CandidateStale);
        var currentState = await configurations.GetAsync(cancellationToken);
        if (!FixedEquals(currentState.Fingerprint, candidate.ExpectedFingerprint))
            return new(false, currentState.ProfileConfigured, false, AiCandidateFailure.CandidateStale);

        var current = await profiles.LoadAsync(cancellationToken);
        DeploymentConfiguration? updated = null;
        if (current is not null)
        {
            try
            {
                updated = validator.ValidateConfiguration(current with
                {
                    Profile = current.Profile with
                    {
                        Ai = current.Profile.Ai with { Provider = candidate.Provider, Model = candidate.Model }
                    }
                }, "AI model candidate", "persisted SQLite policy");
            }
            catch (ConfigurationValidationException)
            {
                return new(false, true, false, AiCandidateFailure.ConfirmationConflict);
            }
            if (!runtimeConfiguration.TryBeginManagementChange())
                return new(false, true, false, AiCandidateFailure.ConfirmationConflict);
        }

        try
        {
            var result = await configurations.CommitValidatedAsync(updated, candidate.Provider, candidate.Model,
                candidate.ExpectedFingerprint, candidate.CredentialUpdatedAtUtc, actor,
                clock.UtcNow.ToUniversalTime(), cancellationToken);
            if (current is not null)
            {
                if (result.Status is AiConfigurationCommitStatus.Committed or AiConfigurationCommitStatus.AlreadyCurrent)
                {
                    var activation = await activator.ActivateAuthoritativeAsync(actor,
                        ["ai.provider", "ai.model", "ai.credentialRevision"], cancellationToken);
                    runtimeConfiguration.CompleteManagementChange(false);
                    if (!activation.Succeeded)
                        return new(false, true, false, AiCandidateFailure.ActivationFailed);
                }
                else
                    runtimeConfiguration.CompleteManagementChange(false);
            }
            return result.Status is AiConfigurationCommitStatus.Committed or AiConfigurationCommitStatus.AlreadyCurrent
                ? new(true, result.ProfileConfigured, false)
                : new(false, result.ProfileConfigured, false, AiCandidateFailure.CandidateStale);
        }
        catch
        {
            if (current is not null) runtimeConfiguration.CompleteManagementChange(false);
            throw;
        }
    }

    private async Task<(IAiManagementClient? Client, AiManagementFailure? Failure, DateTimeOffset? CredentialUpdatedAtUtc)> PrepareClientAsync(
        string provider, CancellationToken cancellationToken)
    {
        var slot = AiProviderNames.CredentialSlot(provider);
        var metadata = await secrets.GetMetadataAsync(slot, cancellationToken);
        if (!metadata.Configured) return (null, AiManagementFailure.CredentialUnavailable, null);
        if (metadata.VerificationStatus != CredentialVerificationStatus.Verified)
            return (null, AiManagementFailure.CredentialNotVerified, metadata.UpdatedAtUtc);
        var resolution = await secrets.ResolveAsync(slot, cancellationToken);
        return resolution.Availability == SecretAvailability.Available
            ? (clients.Create(provider, resolution.Secret!, TimeSpan.FromSeconds(30)), null, metadata.UpdatedAtUtc)
            : (null, AiManagementFailure.CredentialUnavailable, metadata.UpdatedAtUtc);
    }

    private async Task<AiCredentialVerificationResult> RecordVerificationFailureAsync(
        string provider, CredentialSlot slot, AiManagementFailure failure, AuditActor actor,
        DateTimeOffset? expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        var metadata = await secrets.GetMetadataAsync(slot, cancellationToken);
        if (metadata.Configured && expectedUpdatedAtUtc is { } revision &&
            !await secrets.TrySetVerificationIfCurrentAsync(slot, revision,
                CredentialVerificationStatus.Failed, null, Diagnostic(failure), actor, cancellationToken))
            return await RecordStaleCredentialAsync(provider, actor, cancellationToken);
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow, "AiCredentialVerificationFailed",
            $"{provider}:{failure}", ["provider", "verificationStatus", "safeDiagnostic"]), cancellationToken);
        return new(false, failure);
    }

    private async Task<AiCredentialVerificationResult> RecordStaleCredentialAsync(
        string provider, AuditActor actor, CancellationToken cancellationToken)
    {
        await audit.AppendAsync(NewAudit(actor, clock.UtcNow, "AiCredentialVerificationStale",
            provider, ["provider", "credentialRevision"]), cancellationToken);
        return new(false, AiManagementFailure.CredentialChanged);
    }

    private async Task<bool> CredentialStillCurrentAsync(
        string provider, DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        if (expected is null) return false;
        var current = await secrets.GetMetadataAsync(AiProviderNames.CredentialSlot(provider), cancellationToken);
        return current.UpdatedAtUtc == expected &&
               current.VerificationStatus == CredentialVerificationStatus.Verified;
    }

    private static CredentialVerificationDiagnostic Diagnostic(AiManagementFailure failure) => failure switch
    {
        AiManagementFailure.AuthenticationFailed => CredentialVerificationDiagnostic.AuthenticationRejected,
        AiManagementFailure.AuthorizationFailed => CredentialVerificationDiagnostic.PermissionDenied,
        AiManagementFailure.RateLimited => CredentialVerificationDiagnostic.RateLimited,
        AiManagementFailure.Timeout => CredentialVerificationDiagnostic.Timeout,
        AiManagementFailure.ProviderUnavailable => CredentialVerificationDiagnostic.ProviderUnavailable,
        _ => CredentialVerificationDiagnostic.Unknown
    };

    private string? SafeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray()).Trim();
        return string.IsNullOrEmpty(normalized) ? null : redactor.Redact(normalized).Content;
    }

    private static bool IsValidModelId(string value) =>
        ModelIdentifier.IsMatch(value) && !value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProvider(string provider) =>
        AiProviderNames.TryNormalize(provider, out var normalized)
            ? normalized
            : throw new ArgumentException("Unsupported AI provider.", nameof(provider));

    private void InvalidateProviderCandidates(string provider)
    {
        foreach (var entry in candidates)
            if (entry.Value.Provider == provider) candidates.TryRemove(entry.Key, out _);
    }

    private void InvalidateActorCandidates(Guid actorId)
    {
        foreach (var entry in candidates)
            if (entry.Value.ActorId == actorId) candidates.TryRemove(entry.Key, out _);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in candidates)
            if (entry.Value.ExpiresAtUtc <= now) candidates.TryRemove(entry.Key, out _);
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static ControlPlaneAuditRecord NewAudit(
        AuditActor actor, DateTimeOffset at, string operation, string target, IReadOnlyList<string> fields) =>
        new(Guid.NewGuid(), at.ToUniversalTime(), actor, operation, "AiManagement", target, fields);

    private sealed record Candidate(
        string Provider,
        string Model,
        string? DisplayName,
        string ExpectedFingerprint,
        DateTimeOffset CredentialUpdatedAtUtc,
        Guid ActorId,
        DateTimeOffset ExpiresAtUtc);
}

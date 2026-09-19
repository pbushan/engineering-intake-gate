using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Application.Discovery;

/// <summary>
/// Coordinates one full saved-query discovery cycle. Query membership remains authoritative;
/// durable registration happens before the application checkpoint can move.
/// </summary>
public sealed class IncrementalRunService(
    IWorkItemSource workItemSource,
    IWorkItemEligibilityEvaluator eligibilityEvaluator,
    IntakeRunService intakeRunService,
    IAuditRepository auditRepository,
    IIncrementalDiscoveryRepository discoveryRepository,
    IClock clock,
    IWorkItemReadLog log,
    MutationReconciliationService? reconciliationService = null)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    public async Task<IncrementalRunResult> ExecuteAsync(
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(configuration, triggerType, AuditActor.System, cancellationToken);

    public async Task<IncrementalRunResult> ExecuteAsync(
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        AuditActor triggeredBy,
        CancellationToken cancellationToken = default)
    {
        if (triggerType is not (RunTriggerType.Scheduled or RunTriggerType.ManualIncremental))
            throw new ArgumentOutOfRangeException(nameof(triggerType), "Incremental execution requires SCHEDULED or MANUAL_INCREMENTAL.");

        var profile = configuration.Profile;
        var runId = Guid.NewGuid();
        var startedAt = UtcNow();
        var lease = await discoveryRepository.TryAcquireRunLeaseAsync(profile.Identity.Id, runId, startedAt, LeaseDuration, cancellationToken);
        if (lease is null) return IncrementalRunResult.Rejected(triggerType, profile.Processing.ExecutionMode);
        using var leaseCancellation = new CancellationTokenSource();
        var leaseHeartbeat = MaintainLeaseAsync(profile.Identity.Id, runId, leaseCancellation.Token);

        try
        {
            // Reconciliation runs under the existing profile lease, before fresh discovery. It
            // never evaluates AI; any unsafe reconciliation failure stops new enforcement.
            if (reconciliationService is not null)
                await reconciliationService.ReconcilePendingAsync(configuration, cancellationToken);
            var checkpointBefore = await discoveryRepository.GetCheckpointAsync(profile.Identity.Id, cancellationToken);
            var running = NewAudit(IncrementalRunStatus.Running, null, checkpointBefore?.AdvancedAtUtc, null, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null);
            await discoveryRepository.SaveRunAuditAsync(running, cancellationToken);

            var query = await workItemSource.ExecuteSavedQueryAsync(cancellationToken);
            log.Discovery(runId, null, query.Succeeded, query.WorkItemIds.Count, query.Failure?.SafeCategory);
            if (!query.Succeeded)
            {
                var failed = NewAudit(IncrementalRunStatus.Error, UtcNow(), checkpointBefore?.AdvancedAtUtc, checkpointBefore?.AdvancedAtUtc,
                    0, 0, 0, 0, 0, 0, 1, 0, null, null, query.Failure!.SafeCategory);
                await discoveryRepository.SaveRunAuditAsync(failed, cancellationToken);
                return Result(failed);
            }

            var registrations = await discoveryRepository.GetRegistrationsAsync(profile.Identity.Id, cancellationToken);
            var known = registrations.Select(item => item.WorkItemId).ToHashSet();
            var registrationById = registrations.ToDictionary(item => item.WorkItemId);
            var pending = registrations.Where(item => item.ProcessingState is RegisteredWorkState.Pending or RegisteredWorkState.Processing or RegisteredWorkState.Error)
                .Select(item => item.WorkItemId).ToHashSet();
            var queryIds = query.WorkItemIds.Distinct().Order().ToArray();
            var bootstrapCutoff = checkpointBefore is null ? startedAt - profile.Schedule.InitialLookback : (DateTimeOffset?)null;
            var newlyDiscovered = new HashSet<int>();
            var incomplete = new HashSet<int>();
            var bootstrapSuppressed = new HashSet<int>();
            var discoveryReadErrors = 0;

            // The first pass only obtains transient current state needed to select bootstrap and
            // incomplete work. Raw work-item payloads are never persisted by discovery.
            foreach (var id in queryIds)
            {
                var read = await workItemSource.GetWorkItemAsync(id, cancellationToken);
                if (!read.Succeeded)
                {
                    discoveryReadErrors++;
                    if (bootstrapCutoff is not null && !known.Contains(id)) bootstrapSuppressed.Add(id);
                    continue;
                }
                var item = read.WorkItem!;
                if (!known.Contains(id) && (bootstrapCutoff is null || item.ChangedAtUtc is { } changed && changed >= bootstrapCutoff))
                    newlyDiscovered.Add(id);
                else if (!known.Contains(id) && bootstrapCutoff is not null)
                    bootstrapSuppressed.Add(id);
                if (item.Tags.Contains(profile.IntakeState.IncompleteTag, StringComparer.OrdinalIgnoreCase))
                    incomplete.Add(id);
            }

            var selections = queryIds.Where(id => newlyDiscovered.Contains(id) || incomplete.Contains(id) || pending.Contains(id) || bootstrapSuppressed.Contains(id))
                .Select(id => new DiscoveryRegistrationRequest(id,
                    pending.Contains(id) ? registrationById[id].SelectionReason :
                    newlyDiscovered.Contains(id) ? DiscoverySelectionReason.NewlyDiscovered :
                    incomplete.Contains(id) ? DiscoverySelectionReason.IncompleteReevaluation : DiscoverySelectionReason.BootstrapOutsideLookback,
                    bootstrapSuppressed.Contains(id) && !incomplete.Contains(id) ? RegisteredWorkState.Completed : RegisteredWorkState.Pending))
                .ToArray();

            // DISC-004: registration and checkpoint advancement are a single SQLite transaction.
            // A crash before commit leaves no checkpoint advancement; a crash after commit leaves
            // pending registrations that are independently recoverable on the next successful query.
            var checkpointAt = UtcNow();
            if (configuration.RuntimeGenerationId is { } generationId)
                await discoveryRepository.RegisterAndAdvanceCheckpointForGenerationAsync(
                    profile.Identity.Id, generationId, runId, selections, checkpointAt, cancellationToken);
            else
                await discoveryRepository.RegisterAndAdvanceCheckpointAsync(
                    profile.Identity.Id, runId, selections, checkpointAt, cancellationToken);
            await discoveryRepository.RefreshRunLeaseAsync(profile.Identity.Id, runId, UtcNow(), LeaseDuration, cancellationToken);

            var processable = (await discoveryRepository.GetProcessableRegistrationsAsync(profile.Identity.Id, cancellationToken))
                .Where(item => queryIds.Contains(item.WorkItemId)).ToArray();
            var outcomes = await ProcessAsync(processable, queryIds, configuration, triggerType, triggeredBy, runId, cancellationToken);
            var tokenUsage = SumUsage(outcomes.Select(outcome => outcome.TokenUsage));
            var costRelevant = outcomes.Where(outcome => outcome.CostRelevant).ToArray();
            var cost = EstimatedCostAggregation.Sum(
                costRelevant.Select(outcome => outcome.EstimatedCost).ToArray(),
                costRelevant.Select(outcome => outcome.CostInteractionCount).ToArray());
            var errorCount = outcomes.Count(outcome => outcome.Error) + discoveryReadErrors;
            var status = errorCount == 0 ? IncrementalRunStatus.Completed : IncrementalRunStatus.CompletedWithErrors;
            var completed = NewAudit(status, UtcNow(), checkpointBefore?.AdvancedAtUtc, checkpointAt,
                queryIds.Length, newlyDiscovered.Count, incomplete.Count, outcomes.Count, outcomes.Count(outcome => outcome.Pass),
                outcomes.Count(outcome => outcome.Fail), errorCount, outcomes.Count(outcome => outcome.Skipped), tokenUsage, cost,
                discoveryReadErrors == 0 ? null : "WorkItemReadFailed");
            await discoveryRepository.SaveRunAuditAsync(completed, cancellationToken);
            return Result(completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Safe category only; discovery state remains unchanged unless its atomic registration
            // transaction committed. Pending registrations remain available for later recovery.
            var failed = NewAudit(IncrementalRunStatus.Error, UtcNow(), null, null, 0, 0, 0, 0, 0, 0, 1, 0, null, null, "IncrementalRunFailure");
            try { await discoveryRepository.SaveRunAuditAsync(failed, CancellationToken.None); } catch { }
            return Result(failed);
        }
        finally
        {
            leaseCancellation.Cancel();
            try { await leaseHeartbeat; } catch (OperationCanceledException) { }
            try { await discoveryRepository.ReleaseRunLeaseAsync(profile.Identity.Id, runId, CancellationToken.None); } catch { }
        }

        DateTimeOffset UtcNow() => clock.UtcNow.ToUniversalTime();
        IncrementalRunAuditRecord NewAudit(IncrementalRunStatus status, DateTimeOffset? completedAt, DateTimeOffset? before, DateTimeOffset? after,
            int queryCount, int newly, int incompleteCount, int processed, int pass, int fail, int error, int skipped,
            TokenUsage? usage, EstimatedCost? estimatedCost, string? errorCategory) => new(
                runId, triggerType, startedAt, completedAt, profile.Processing.ExecutionMode, profile.Identity.Id, profile.Ado.SavedQueryId,
                before, after, queryCount, newly, incompleteCount, processed, pass, fail, error, skipped, usage, estimatedCost, status, errorCategory)
            { ConfigurationGenerationId = configuration.RuntimeGenerationId, TriggeredBy = triggeredBy };
    }

    private async Task MaintainLeaseAsync(string profileId, Guid runId, CancellationToken cancellationToken)
    {
        // Keep a long batch from silently becoming stale while it is still processing. A failed
        // refresh does not extend the lease; the existing safety boundary remains fail-closed on
        // the next active-run attempt rather than creating a permanent lock.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await discoveryRepository.RefreshRunLeaseAsync(profileId, runId, clock.UtcNow.ToUniversalTime(), LeaseDuration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return; }
        }
    }

    private async Task<IReadOnlyList<ProcessingOutcome>> ProcessAsync(
        IReadOnlyList<DiscoveredWorkRegistration> registrations,
        IReadOnlyCollection<int> queryIds,
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        AuditActor triggeredBy,
        Guid discoveryRunId,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(configuration.Profile.Processing.Concurrency, configuration.Profile.Processing.Concurrency);
        var tasks = registrations.Select(async registration =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try { return await ProcessOneAsync(registration, queryIds, configuration, triggerType, triggeredBy, discoveryRunId, cancellationToken); }
            finally { semaphore.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<ProcessingOutcome> ProcessOneAsync(DiscoveredWorkRegistration registration, IReadOnlyCollection<int> queryIds,
        DeploymentConfiguration configuration, RunTriggerType triggerType, AuditActor triggeredBy,
        Guid discoveryRunId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        await SetProcessingStateAsync(RegisteredWorkState.Processing, now);
        var read = await workItemSource.GetWorkItemAsync(registration.WorkItemId, cancellationToken);
        if (!read.Succeeded)
        {
            await SetProcessingStateAsync(RegisteredWorkState.Error, clock.UtcNow.ToUniversalTime());
            return new ProcessingOutcome(Error: true);
        }

        var item = read.WorkItem!;
        var exclusion = eligibilityEvaluator.FindExclusion(item, configuration.Profile.Exclusions);
        if (exclusion is not null)
        {
            var reason = $"ExcludedByRule:{exclusion.RuleId}";
            log.Eligibility(discoveryRunId, registration.WorkItemId, WorkItemEligibility.NotEligible, reason);
            if (!await PersistNotEligibleAsync(item, registration.SelectionReason.ToString(), reason,
                    configuration, triggerType, triggeredBy, discoveryRunId, cancellationToken))
            {
                await SetProcessingStateAsync(RegisteredWorkState.Error, clock.UtcNow.ToUniversalTime());
                return new ProcessingOutcome(Error: true);
            }
            await SetProcessingStateAsync(RegisteredWorkState.Completed, clock.UtcNow.ToUniversalTime());
            return new ProcessingOutcome(Skipped: true);
        }

        // A failed historical evaluation is not a perpetual retry signal. The current incomplete
        // tag is the re-evaluation signal; unprocessed newly-discovered work is still processed.
        if (registration.SelectionReason == DiscoverySelectionReason.IncompleteReevaluation &&
            !item.Tags.Contains(configuration.Profile.IntakeState.IncompleteTag, StringComparer.OrdinalIgnoreCase))
        {
            await SetProcessingStateAsync(RegisteredWorkState.Completed, clock.UtcNow.ToUniversalTime());
            return new ProcessingOutcome(Skipped: true);
        }

        log.Eligibility(discoveryRunId, registration.WorkItemId, WorkItemEligibility.Eligible, "ConfiguredSavedQueryMember");
        var result = await intakeRunService.ExecuteAsync(item, configuration, triggerType, registration.SelectionReason.ToString(), Guid.NewGuid(),
            WorkItemEligibility.Eligible, configuration.Profile.Ado.SavedQueryId, null, triggeredBy,
            discoveryRunId, cancellationToken);
        var audit = await auditRepository.GetEvaluationAsync(result.EvaluationId, cancellationToken);
        await SetProcessingStateAsync(
            result.ProcessingStatus == Evaluation.EvaluationProcessingStatus.Completed ? RegisteredWorkState.Completed : RegisteredWorkState.Error,
            clock.UtcNow.ToUniversalTime());
        return new ProcessingOutcome(result.Decision == Evaluation.IntakeDecision.Pass, result.Decision == Evaluation.IntakeDecision.Fail,
            result.ProcessingStatus == Evaluation.EvaluationProcessingStatus.Error, false, true,
            audit?.AiInteractions.Count ?? 0, audit?.TokenUsage, audit?.EstimatedCost);

        Task SetProcessingStateAsync(RegisteredWorkState state, DateTimeOffset at) =>
            configuration.RuntimeGenerationId is { } generationId
                ? discoveryRepository.SetProcessingStateForGenerationAsync(registration.ProfileId,
                    generationId, registration.WorkItemId, state, at, cancellationToken)
                : discoveryRepository.SetProcessingStateAsync(registration.ProfileId,
                    registration.WorkItemId, state, at, cancellationToken);
    }

    private async Task<bool> PersistNotEligibleAsync(
        RawWorkItem workItem,
        string selectionReason,
        string exclusionReason,
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        AuditActor triggeredBy,
        Guid parentRunId,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var evaluationId = Guid.NewGuid().ToString("D");
        var now = clock.UtcNow.ToUniversalTime();
        var run = new RunAuditRecord(
            runId, triggerType, now, now, configuration.Profile.Processing.ExecutionMode,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Version,
            configuration.PolicyFingerprint, Evaluation.EvaluationProcessingStatus.Completed,
            1, 0, 0, 0, 1, null, null, null)
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            SavedQueryId = configuration.Profile.Ado.SavedQueryId,
            Eligibility = WorkItemEligibility.NotEligible,
            TriggeredBy = triggeredBy,
            ParentRunId = parentRunId
        };
        var evaluation = new EvaluationAuditRecord(
            evaluationId, runId, workItem.WorkItemId, workItem.Revision, now, selectionReason,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Id,
            configuration.Policy.Identity.Version, configuration.PolicyFingerprint,
            Evaluation.EvaluatorPrompt.Version, configuration.Profile.Ai.Provider, configuration.Profile.Ai.Model,
            [], [], [], [], null, false, 0, [], 0, 0, 0, 0, false, false, null,
            Evaluation.EvaluationProcessingStatus.Completed, configuration.Profile.Processing.ExecutionMode,
            null, null, [], [], [], [])
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            SavedQueryId = configuration.Profile.Ado.SavedQueryId,
            Eligibility = WorkItemEligibility.NotEligible,
            ExclusionReason = exclusionReason,
            TriggeredBy = triggeredBy,
            ParentRunId = parentRunId
        };
        try
        {
            await auditRepository.SaveAsync(run, evaluation, cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static IncrementalRunResult Result(IncrementalRunAuditRecord audit) => new(audit.RunId, audit.TriggerType, audit.Status,
        audit.ExecutionMode, audit.StartedAtUtc, true, audit.ErrorCategory);
    private static TokenUsage? SumUsage(IEnumerable<TokenUsage?> all)
    {
        var values = all.Where(value => value is not null).Cast<TokenUsage>().ToArray();
        return values.Length == 0 ? null : new TokenUsage(values.Sum(value => value.InputTokens), values.Sum(value => value.OutputTokens), values.Sum(value => value.TotalTokens));
    }
    private sealed record ProcessingOutcome(bool Pass = false, bool Fail = false, bool Error = false, bool Skipped = false,
        bool CostRelevant = false,
        int CostInteractionCount = 0,
        TokenUsage? TokenUsage = null, EstimatedCost? EstimatedCost = null);
}

using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Application.Audit;

public interface IRunAuditLog
{
    void Decision(Guid runId, string evaluationId, IntakeDecision? decision, EvaluationProcessingStatus status, ExecutionMode mode, int proposedMutationCount);
    void Audit(Guid runId, string evaluationId, bool succeeded, string? failureCategory);
}

public sealed class NullRunAuditLog : IRunAuditLog
{
    public void Decision(Guid runId, string evaluationId, IntakeDecision? decision, EvaluationProcessingStatus status, ExecutionMode mode, int proposedMutationCount) { }
    public void Audit(Guid runId, string evaluationId, bool succeeded, string? failureCategory) { }
}

public sealed record IntakeRunResult(
    Guid RunId,
    string EvaluationId,
    IntakeDecision? Decision,
    EvaluationProcessingStatus ProcessingStatus,
    ExecutionMode ExecutionMode,
    IReadOnlyList<ProposedMutation> ProposedMutations,
    IReadOnlyList<ProposedMutation> AttemptedMutations,
    IReadOnlyList<MutationOutcome> MutationOutcomes,
    string? ErrorCategory);

public sealed class LiveExecutionUnavailableException()
    : InvalidOperationException("LIVE execution is unavailable because the required guarded work-item source and writer are not configured.");

public sealed class IntakeRunService
{
    private readonly IEvidencePreprocessor preprocessor;
    private readonly IIntakeEvaluationService evaluationService;
    private readonly IIntakeDecisionHandler decisionHandler;
    private readonly IAuditRepository auditRepository;
    private readonly IClock clock;
    private readonly IRunAuditLog log;
    private readonly ICostEstimator costEstimator;
    private readonly IWorkItemSource? workItemSource;
    private readonly IWorkItemWriter? workItemWriter;

    public IntakeRunService(
        IEvidencePreprocessor preprocessor,
        IIntakeEvaluationService evaluationService,
        IIntakeDecisionHandler decisionHandler,
        IAuditRepository auditRepository,
        IClock clock,
        IRunAuditLog log)
        : this(preprocessor, evaluationService, decisionHandler, auditRepository, clock, log, new CostEstimator()) { }

    public IntakeRunService(
        IEvidencePreprocessor preprocessor,
        IIntakeEvaluationService evaluationService,
        IIntakeDecisionHandler decisionHandler,
        IAuditRepository auditRepository,
        IClock clock,
        IRunAuditLog log,
        ICostEstimator costEstimator)
        : this(preprocessor, evaluationService, decisionHandler, auditRepository, clock, log, costEstimator, null, null) { }

    public IntakeRunService(
        IEvidencePreprocessor preprocessor,
        IIntakeEvaluationService evaluationService,
        IIntakeDecisionHandler decisionHandler,
        IAuditRepository auditRepository,
        IClock clock,
        IRunAuditLog log,
        ICostEstimator costEstimator,
        IWorkItemSource? workItemSource,
        IWorkItemWriter? workItemWriter)
    {
        this.preprocessor = preprocessor;
        this.evaluationService = evaluationService;
        this.decisionHandler = decisionHandler;
        this.auditRepository = auditRepository;
        this.clock = clock;
        this.log = log;
        this.costEstimator = costEstimator;
        this.workItemSource = workItemSource;
        this.workItemWriter = workItemWriter;
    }

    public async Task<IntakeRunResult> ExecuteAsync(
        RawWorkItem workItem,
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        string selectionReason,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workItem, configuration, triggerType, selectionReason, Guid.NewGuid(),
            WorkItemEligibility.Eligible, configuration.Profile.Ado.SavedQueryId, null, cancellationToken);

    public async Task<IntakeRunResult> ExecuteAsync(
        RawWorkItem workItem,
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        string selectionReason,
        Guid runId,
        WorkItemEligibility eligibility,
        Guid? savedQueryId,
        string? exclusionReason,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(workItem, configuration, triggerType, selectionReason, runId, eligibility,
            savedQueryId, exclusionReason, AuditActor.System, null, cancellationToken);

    public async Task<IntakeRunResult> ExecuteAsync(
        RawWorkItem workItem,
        DeploymentConfiguration configuration,
        RunTriggerType triggerType,
        string selectionReason,
        Guid runId,
        WorkItemEligibility eligibility,
        Guid? savedQueryId,
        string? exclusionReason,
        AuditActor triggeredBy,
        Guid? parentRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);
        if (configuration.Profile.Processing.ExecutionMode == ExecutionMode.Live && (workItemSource is null || workItemWriter is null))
        {
            throw new LiveExecutionUnavailableException();
        }

        var evaluationId = Guid.NewGuid().ToString("D");
        var startedAt = clock.UtcNow.ToUniversalTime();
        var evidence = await preprocessor.PrepareAsync(workItem, configuration.Profile.Processing, cancellationToken);
        var evaluation = await evaluationService.EvaluateAsync(evidence, configuration, evaluationId, cancellationToken);
        var decision = decisionHandler.Decide(
            evaluation,
            workItem.Tags,
            configuration.Profile.IntakeState,
            configuration.Profile.IntakePolicy.Url);

        log.Decision(runId, evaluationId, evaluation.Outcome, evaluation.ProcessingStatus,
            configuration.Profile.Processing.ExecutionMode, decision.ProposedMutations.Count);

        var completedAt = clock.UtcNow.ToUniversalTime();
        var isPass = evaluation.Outcome == IntakeDecision.Pass;
        var isFail = evaluation.Outcome == IntakeDecision.Fail;
        var isError = evaluation.ProcessingStatus == EvaluationProcessingStatus.Error;
        var estimatedCost = costEstimator.Estimate(configuration.Profile.Ai, configuration.Profile.Ai.Model, evaluation.TokenUsage);
        var run = new RunAuditRecord(
            runId, triggerType, startedAt, completedAt, configuration.Profile.Processing.ExecutionMode,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Version, configuration.PolicyFingerprint,
            evaluation.ProcessingStatus, 1, isPass ? 1 : 0, isFail ? 1 : 0, isError ? 1 : 0, 0,
            evaluation.TokenUsage, estimatedCost, evaluation.Failure?.Category)
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            SavedQueryId = savedQueryId,
            Eligibility = eligibility,
            TriggeredBy = triggeredBy,
            ParentRunId = parentRunId
        };

        var trusted = evaluation.Result;
        var audit = new EvaluationAuditRecord(
            evaluationId, runId, evidence.WorkItemId, evidence.Revision, completedAt, selectionReason,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Id, configuration.Policy.Identity.Version,
            configuration.PolicyFingerprint, EvaluatorPrompt.Version, configuration.Profile.Ai.Provider,
            configuration.Profile.Ai.Model, trusted?.ApplicableCriteria ?? [], trusted?.SatisfiedCriteria ?? [],
            trusted?.Deficiencies ?? [], trusted?.Ambiguities ?? [], trusted?.EngineeringSummary,
            evidence.Redaction.RedactionOccurred, evidence.Redaction.RedactionCount,
            evidence.Redaction.CategoryCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RedactionCategoryCount(pair.Key, pair.Value)).ToArray(),
            evidence.Processing.IncludedAttachmentMetadataCount,
            evidence.Processing.AvailableAttachmentMetadataCount,
            evidence.Processing.TruncatedAttachmentMetadataCount,
            evidence.Processing.OmittedAttachmentMetadataCount,
            evidence.Processing.AttachmentMetadataAvailable,
            evidence.Processing.AttachmentContentInspected,
            evaluation.Outcome, evaluation.ProcessingStatus, configuration.Profile.Processing.ExecutionMode,
            evaluation.TokenUsage, estimatedCost, decision.ProposedMutations, [], [],
            evaluation.Failure is null ? [] : [evaluation.Failure.Category])
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            ProviderReportedModel = evaluation.ProviderReportedModel,
            ProviderRequestIds = evaluation.ProviderRequestIds,
            SavedQueryId = savedQueryId,
            Eligibility = eligibility,
            ExclusionReason = exclusionReason,
            TriggeredBy = triggeredBy,
            ParentRunId = parentRunId,
            AttachmentProcessing = evidence.Attachments.Select(attachment => new AttachmentProcessingAuditRecord(
                attachment.Reference,
                attachment.FileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.ProcessingStatus,
                attachment.InspectionMode,
                attachment.Truncated,
                attachment.Sampled,
                attachment.PagesAvailable,
                attachment.PagesInspected,
                attachment.FailureCategory)).ToArray()
        };

        try
        {
            await auditRepository.SaveAsync(run, audit, cancellationToken);
            log.Audit(runId, evaluationId, true, null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            log.Audit(runId, evaluationId, false, "AuditPersistenceFailure");
            return new IntakeRunResult(runId, evaluationId, null, EvaluationProcessingStatus.Error,
                configuration.Profile.Processing.ExecutionMode, [], [], [], "AuditPersistenceFailure");
        }

        if (configuration.Profile.Processing.ExecutionMode == ExecutionMode.DryRun ||
            evaluation.ProcessingStatus != EvaluationProcessingStatus.Completed || evaluation.Result is null)
        {
            return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, evaluation.ProcessingStatus,
                configuration.Profile.Processing.ExecutionMode, decision.ProposedMutations, [], [], evaluation.Failure?.Category);
        }

        return await ApplyLiveMutationsAsync(
            runId, workItem, evidence, evaluation, configuration, evaluationId, decision.ProposedMutations, cancellationToken);
    }

    private async Task<IntakeRunResult> ApplyLiveMutationsAsync(
        Guid runId,
        RawWorkItem evaluatedWorkItem,
        EvaluationEvidence evidence,
        EvaluationProcessingResult evaluation,
        DeploymentConfiguration configuration,
        string evaluationId,
        IReadOnlyList<ProposedMutation> proposed,
        CancellationToken cancellationToken)
    {
        // SAFE-001: this fresh provider read is the mandatory gate before every first write.
        var currentRead = await workItemSource!.GetWorkItemAsync(int.Parse(evaluatedWorkItem.WorkItemId, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        if (!currentRead.Succeeded)
            return await CompleteWithoutWriteAsync("CurrentWorkItemReadFailed", MutationExecutionState.Failed, currentRead.Failure!.SafeCategory);
        var current = currentRead.WorkItem!;
        if (!string.Equals(current.Revision, evidence.Revision, StringComparison.Ordinal))
            return await CompleteWithoutWriteAsync("StaleRevisionReevaluationRequired", MutationExecutionState.StaleReevaluationRequired, "StaleRevisionReevaluationRequired");

        var currentDecision = decisionHandler.Decide(evaluation, current.Tags, configuration.Profile.IntakeState, configuration.Profile.IntakePolicy.Url);
        var tagMutations = currentDecision.ProposedMutations.Where(m => m.Type != ProposedMutationType.PostComment).ToArray();
        var commentMutation = currentDecision.ProposedMutations.SingleOrDefault(m => m.Type == ProposedMutationType.PostComment);
        var attempted = new List<ProposedMutation>();
        var outcomes = new List<MutationOutcome>();
        var expectedCommentRevision = current.Revision;
        bool duplicateComment;
        try
        {
            // History is required before any write: a repository failure must not turn into
            // duplicate comments or an un-auditable live enforcement attempt.
            duplicateComment = commentMutation is not null && await IsDuplicateCommentAsync(
                current.WorkItemId, evaluation.Result!, configuration.Profile.Identity.Id, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CompleteWithoutWriteAsync("MutationAuditHistoryUnavailable", MutationExecutionState.Failed, "AuditPersistenceFailure");
        }

        if (commentMutation is not null && !await TryPersistSuppressionAuditAsync(
                evaluationId, duplicateComment, duplicateComment ? "MateriallyUnchangedAssessment" : null,
                !duplicateComment, cancellationToken))
            return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Error,
                ExecutionMode.Live, proposed, [], [], "AuditPersistenceFailure");

        if (tagMutations.Length > 0)
        {
            if (!await CreateReconciliationSafetyRecordAsync(current, evidence, evaluationId, proposed, tagMutations, configuration, cancellationToken))
                return new IntakeRunResult(runId, evaluationId, null, EvaluationProcessingStatus.Error, ExecutionMode.Live, [], [], [], "PersistenceUnavailable");
            var finalTags = ApplyTags(current.Tags, tagMutations);
            var tagResult = await workItemWriter!.UpdateIntakeTagsAsync(
                new IntakeTagUpdateRequest(int.Parse(current.WorkItemId, System.Globalization.CultureInfo.InvariantCulture), current.Revision, finalTags), cancellationToken);
            attempted.AddRange(tagMutations);
            foreach (var mutation in tagMutations) outcomes.Add(Outcome(mutation, tagResult));
            if (!await TryPersistMutationAuditAsync(evaluationId, attempted, outcomes,
                    tagResult.Succeeded ? MutationExecutionState.Completed : StateFor(tagResult), tagResult.SafeErrorCategory, cancellationToken))
                return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Error, ExecutionMode.Live, proposed, attempted, outcomes, "AuditPersistenceFailure");
            if (!tagResult.Succeeded)
                return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Completed, ExecutionMode.Live, proposed, attempted, outcomes, tagResult.SafeErrorCategory);
            if (string.IsNullOrWhiteSpace(tagResult.ResultingRevision))
                return await CompleteWithoutWriteAsync("MutationResponseMissingRevision", MutationExecutionState.Failed, "AzureDevOpsInvalidMutationResponse", attempted, outcomes);
            if (!await MarkTagProgressForReconciliationAsync(evaluationId, current, evidence, proposed, tagMutations,
                    tagResult.ResultingRevision, configuration, cancellationToken))
                return new IntakeRunResult(runId, evaluationId, null, EvaluationProcessingStatus.Error, ExecutionMode.Live, [], attempted, outcomes, "PersistenceUnavailable");
            expectedCommentRevision = tagResult.ResultingRevision;
        }

        if (commentMutation is not null && !duplicateComment)
        {
            if (tagMutations.Length == 0 && !await CreateReconciliationSafetyRecordAsync(current, evidence, evaluationId, proposed, [], configuration, cancellationToken))
                return new IntakeRunResult(runId, evaluationId, null, EvaluationProcessingStatus.Error, ExecutionMode.Live, [], [], [], "PersistenceUnavailable");
            // The comment endpoint has no JSON-Patch revision test. Re-read immediately before it;
            // the adapter also sends If-Match as a provider precondition where it is honored.
            var beforeComment = await workItemSource.GetWorkItemAsync(int.Parse(current.WorkItemId, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            var commentCurrent = beforeComment.WorkItem;
            if (!beforeComment.Succeeded || commentCurrent is null || !string.Equals(commentCurrent.Revision, expectedCommentRevision, StringComparison.Ordinal))
                return await CompleteWithoutWriteAsync("StaleRevisionReevaluationRequired", MutationExecutionState.StaleReevaluationRequired, "StaleRevisionReevaluationRequired", attempted, outcomes);
            var commentResult = await workItemWriter!.AddValidatorCommentAsync(new ValidatorCommentRequest(
                int.Parse(commentCurrent.WorkItemId, System.Globalization.CultureInfo.InvariantCulture), commentCurrent.Revision,
                commentMutation.Body!, commentMutation.CommentMarker!), cancellationToken);
            attempted.Add(commentMutation);
            outcomes.Add(Outcome(commentMutation, commentResult));
            await TryPersistMutationAuditAsync(evaluationId, attempted, outcomes,
                commentResult.Succeeded ? MutationExecutionState.Completed : StateFor(commentResult), commentResult.SafeErrorCategory, cancellationToken);
            return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Completed,
                ExecutionMode.Live, proposed, attempted, outcomes, commentResult.SafeErrorCategory);
        }

        await TryPersistMutationAuditAsync(evaluationId, attempted, outcomes, MutationExecutionState.Completed, null, cancellationToken);
        return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Completed,
            ExecutionMode.Live, proposed, attempted, outcomes, null);

        async Task<IntakeRunResult> CompleteWithoutWriteAsync(string _, MutationExecutionState state, string error,
            IReadOnlyList<ProposedMutation>? existingAttempts = null, IReadOnlyList<MutationOutcome>? existingOutcomes = null)
        {
            var safeAttempts = existingAttempts ?? [];
            var safeOutcomes = existingOutcomes ?? [];
            await TryPersistMutationAuditAsync(evaluationId, safeAttempts, safeOutcomes, state, error, cancellationToken);
            return new IntakeRunResult(runId, evaluationId, evaluation.Outcome, EvaluationProcessingStatus.Completed,
                ExecutionMode.Live, proposed, safeAttempts, safeOutcomes, error);
        }
    }

    private async Task<bool> TryPersistMutationAuditAsync(string evaluationId, IReadOnlyList<ProposedMutation> attempted,
        IReadOnlyList<MutationOutcome> outcomes, MutationExecutionState state, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await auditRepository.UpdateMutationAuditAsync(evaluationId, attempted, outcomes, state,
                error is null ? [] : [error], cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<bool> TryPersistSuppressionAuditAsync(
        string evaluationId,
        bool suppressed,
        string? reason,
        bool materiallyChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditRepository.UpdateSuppressionAuditAsync(
                evaluationId, suppressed, reason, materiallyChanged, cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<bool> CreateReconciliationSafetyRecordAsync(
        RawWorkItem current,
        EvaluationEvidence evidence,
        string evaluationId,
        IReadOnlyList<ProposedMutation> proposed,
        IReadOnlyList<ProposedMutation> tagMutations,
        DeploymentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Existing in-memory/unit repositories intentionally do not gain a fake reconciliation
        // capability. LIVE production composition always supplies the durable implementation.
        if (auditRepository is not IReconciliationRepository reconciliations) return true;
        try
        {
            var now = clock.UtcNow.ToUniversalTime();
            await reconciliations.CreatePendingAsync(new MutationReconciliationRecord(
                evaluationId, configuration.Profile.Identity.Id, current.WorkItemId, evidence.Revision, current.Revision,
                proposed, ApplyTags(current.Tags, tagMutations), [], ReconciliationStatus.Pending, now, now, 0, null), cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> MarkTagProgressForReconciliationAsync(
        string evaluationId, RawWorkItem current, EvaluationEvidence evidence, IReadOnlyList<ProposedMutation> proposed,
        IReadOnlyList<ProposedMutation> tagMutations, string resultingRevision, DeploymentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (auditRepository is not IReconciliationRepository reconciliations) return true;
        try
        {
            var now = clock.UtcNow.ToUniversalTime();
            await reconciliations.UpdateAsync(new MutationReconciliationRecord(
                evaluationId, configuration.Profile.Identity.Id, current.WorkItemId, evidence.Revision, resultingRevision,
                proposed, ApplyTags(current.Tags, tagMutations), tagMutations.Select(item => item.Type).Distinct().ToArray(),
                ReconciliationStatus.Pending, now, now, 1, null), cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> IsDuplicateCommentAsync(string workItemId, EvaluationResult result, string profileId, CancellationToken cancellationToken)
    {
        var history = await auditRepository.GetWorkItemHistoryAsync(workItemId, profileId, cancellationToken);
        // Audit, not ADO prose or manually managed tags, is authoritative for validator history.
        return history.Any(item => item.Decision == result.Decision &&
            string.Equals(item.PolicyFingerprint, result.PolicyFingerprint, StringComparison.Ordinal) &&
            item.MutationOutcomes.Any(outcome => outcome is { Type: ProposedMutationType.PostComment, Succeeded: true }) &&
            (result.Decision == IntakeDecision.Pass
                ? string.Equals(PassSignature(item), PassSignature(result), StringComparison.Ordinal)
                : string.Equals(DeficiencySignature(item), DeficiencySignature(result), StringComparison.Ordinal)));
    }

    private static IReadOnlyList<string> ApplyTags(IReadOnlyCollection<string> current, IReadOnlyList<ProposedMutation> mutations)
    {
        var tags = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
            if (mutation.Type == ProposedMutationType.RemoveTag) tags.Remove(mutation.Tag!);
            else if (mutation.Type == ProposedMutationType.AddTag) tags.Add(mutation.Tag!);
        return tags.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private MutationOutcome Outcome(ProposedMutation mutation, WorkItemMutationResult result) => new(mutation.Type, result.Succeeded, result.SafeErrorCategory)
    {
        AttemptedAtUtc = clock.UtcNow.ToUniversalTime(),
        ProviderStatusCode = result.ProviderStatusCode,
        SafeTarget = mutation.Type == ProposedMutationType.PostComment ? "validator-comment" : "intake-tags"
    };

    private static MutationExecutionState StateFor(WorkItemMutationResult result) =>
        result.FailureKind == WorkItemMutationFailureKind.Concurrency ? MutationExecutionState.StaleReevaluationRequired : MutationExecutionState.Failed;

    private static string DeficiencySignature(EvaluationAuditRecord audit) => Signature(audit.Deficiencies, audit.Ambiguities);
    private static string DeficiencySignature(EvaluationResult result) => Signature(result.Deficiencies, result.Ambiguities);
    private static string PassSignature(EvaluationAuditRecord audit) =>
        string.Join("|", audit.ApplicableCriteria.Select(id => "a:" + id).Concat(audit.SatisfiedCriteria.Select(id => "s:" + id)).Order(StringComparer.Ordinal));
    private static string PassSignature(EvaluationResult result) =>
        string.Join("|", result.ApplicableCriteria.Select(id => "a:" + id).Concat(result.SatisfiedCriteria.Select(id => "s:" + id)).Order(StringComparer.Ordinal));
    private static string Signature(IReadOnlyList<EvaluationDeficiency> deficiencies, IReadOnlyList<EvaluationAmbiguity> ambiguities) =>
        string.Join("|", deficiencies.Select(d => "d:" + d.CriterionId).Concat(ambiguities.Select(a => "a:" + (a.CriterionId ?? "general"))).Order(StringComparer.Ordinal));
}

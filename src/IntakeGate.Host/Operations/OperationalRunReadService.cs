using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Host.Operations;

public sealed class OperationalRunReadService(
    IOperationalAuditReader reader,
    IRuntimeConfigurationGenerationRepository generations,
    IAnalysisCacheRepository analysisCache,
    IClock clock)
{
    public OperationalRunReadService(
        IOperationalAuditReader reader,
        IRuntimeConfigurationGenerationRepository generations)
        : this(reader, generations, new NullAnalysisCacheRepository(), new SystemClock()) { }

    public async Task<RunHistoryPageResponse> ListAsync(
        OperationalRunQuery query,
        CancellationToken cancellationToken)
    {
        var page = await reader.ListRunsAsync(query, cancellationToken);
        var items = new List<RunSummaryResponse>(page.Runs.Count);
        foreach (var run in page.Runs)
            items.Add(Summary(run));
        return new RunHistoryPageResponse(page.Page, page.PageSize, page.TotalCount,
            page.TotalCount == 0 ? 0 : (int)Math.Ceiling(page.TotalCount / (double)page.PageSize), items);
    }

    public async Task<RunDetailResponse?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reader.GetRunAsync(runId, cancellationToken);
        if (run is null) return null;
        var generation = await GenerationAsync(GenerationId(run), cancellationToken);
        var summary = Summary(run);
        var firstEvaluation = run.Evaluations.FirstOrDefault();
        var savedQueryId = run.IncrementalRun?.SavedQueryId ?? run.ItemRun?.SavedQueryId ?? firstEvaluation?.SavedQueryId;
        var policyVersion = run.ItemRun?.PolicyVersion ?? firstEvaluation?.PolicyVersion ?? string.Empty;
        var policyFingerprint = run.ItemRun?.PolicyFingerprint ?? firstEvaluation?.PolicyFingerprint ?? string.Empty;
        var items = run.Evaluations.Select(evaluation => ItemSummary(evaluation, generation)).ToArray();
        return new RunDetailResponse(
            summary,
            generation?.Configuration.Profile.Identity.Version,
            savedQueryId,
            generation is null ? null : AzureDevOpsConfigurationFingerprint.Create(
                generation.ProfileId, generation.Configuration.Profile.Ado),
            policyVersion,
            policyFingerprint,
            generation?.ConfigurationFingerprint,
            items);
    }

    public async Task<RunItemDetailResponse?> GetItemAsync(
        Guid runId,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        var evaluation = await reader.GetEvaluationAsync(runId, evaluationId, cancellationToken);
        if (evaluation is null) return null;
        var generation = await GenerationAsync(evaluation.ConfigurationGenerationId, cancellationToken);
        var context = evaluation.AnalysisContextId is null ? null :
            await analysisCache.GetContextAsync(evaluation.AnalysisContextId, clock.UtcNow, cancellationToken);
        return ItemDetail(evaluation, generation, context);
    }

    private static RunSummaryResponse Summary(OperationalRunAuditEnvelope envelope)
    {
        var incremental = envelope.IncrementalRun;
        var item = envelope.ItemRun;
        var started = incremental?.StartedAtUtc ?? item!.StartedAtUtc;
        var completed = incremental?.CompletedAtUtc ?? item!.CompletedAtUtc;
        var evaluations = envelope.Evaluations;
        var errors = evaluations.SelectMany(EvaluationErrors).ToList();
        var topError = incremental?.ErrorCategory ?? item?.ErrorCategory;
        if (topError is not null) errors.Add(SafeError(topError));
        errors = errors.DistinctBy(error => error.Category).ToList();

        var pass = incremental?.PassCount ?? item!.PassCount;
        var fail = incremental?.FailCount ?? item!.FailCount;
        var error = incremental?.ErrorCount ?? item!.ErrorCount;
        var skipped = incremental?.SkippedCount ?? item!.SkippedCount;
        var notEligible = incremental is null
            ? (item!.Eligibility == WorkItemEligibility.NotEligible ? item.ItemCount : 0)
            : evaluations.Count(evaluation => evaluation.Eligibility == WorkItemEligibility.NotEligible);
        var status = incremental is not null
            ? MapStatus(incremental.Status)
            : item!.ProcessingStatus == EvaluationProcessingStatus.Error
                ? OperationalRunStatus.Error
                : errors.Count > 0 ? OperationalRunStatus.CompletedWithErrors : OperationalRunStatus.Completed;
        var actor = incremental is null ? item!.TriggeredBy : incremental.TriggeredBy;
        var generationId = incremental is null
            ? item!.ConfigurationGenerationId
            : incremental.ConfigurationGenerationId;
        var usage = incremental is null ? item!.TokenUsage : incremental.TokenUsage;
        var estimatedCost = incremental is null ? item!.EstimatedCost : incremental.EstimatedCost;
        return new RunSummaryResponse(
            envelope.RunId,
            incremental?.TriggerType ?? item!.TriggerType,
            Actor(actor),
            started,
            completed,
            completed is null ? null : Math.Max(0, (long)(completed.Value - started).TotalMilliseconds),
            incremental?.ExecutionMode ?? item!.ExecutionMode,
            status,
            incremental?.ProfileId ?? item!.ProfileId,
            generationId,
            incremental?.QueryResultCount ?? item!.ItemCount,
            incremental?.ProcessedCount ?? (item!.Eligibility == WorkItemEligibility.NotEligible ? 0 : item.ItemCount),
            pass,
            fail,
            error,
            notEligible,
            skipped,
            evaluations.Count(evaluation => evaluation.UpdateSuppressed),
            evaluations.SelectMany(evaluation => evaluation.MutationOutcomes).Count(outcome => outcome.Succeeded),
            Token(usage),
            Cost(estimatedCost),
            errors);
    }

    private static RunItemSummaryResponse ItemSummary(
        EvaluationAuditRecord evaluation,
        RuntimeConfigurationGeneration? generation) => new(
        evaluation.EvaluationId,
        evaluation.WorkItemId,
        AdoUrl(evaluation, generation),
        Decision(evaluation),
        DecisionLabel(Decision(evaluation)),
        evaluation.Eligibility,
        evaluation.ProcessingStatus,
        evaluation.UpdateSuppressed,
        evaluation.ConfigurationGenerationId,
        Token(evaluation.TokenUsage),
        Cost(evaluation.EstimatedCost));

    private static RunItemDetailResponse ItemDetail(
        EvaluationAuditRecord evaluation,
        RuntimeConfigurationGeneration? generation,
        AnalysisContextSnapshot? context)
    {
        var decision = Decision(evaluation);
        return new RunItemDetailResponse(
            evaluation.EvaluationId,
            evaluation.RunId,
            evaluation.ParentRunId,
            evaluation.WorkItemId,
            evaluation.EvaluatedRevision,
            AdoUrl(evaluation, generation),
            decision,
            DecisionLabel(decision),
            evaluation.Eligibility,
            evaluation.ExclusionReason,
            evaluation.ProcessingStatus,
            evaluation.ExecutionMode,
            evaluation.ConfigurationGenerationId,
            evaluation.SelectionReason,
            evaluation.PolicyVersion,
            evaluation.PolicyFingerprint,
            evaluation.PromptVersion,
            evaluation.ProviderIdentifier,
            evaluation.ModelIdentifier,
            evaluation.ApplicableCriteria,
            evaluation.SatisfiedCriteria,
            evaluation.Deficiencies,
            evaluation.Ambiguities,
            evaluation.EngineeringSummary,
            evaluation.ProposedMutations.Select(Effect).ToArray(),
            evaluation.MutationOutcomes.Select(outcome => Attempt(outcome, evaluation.AttemptedMutations)).ToArray(),
            evaluation.MutationOutcomes.Where(outcome => outcome.Succeeded)
                .Select(outcome => Effect(outcome, evaluation.AttemptedMutations)).ToArray(),
            evaluation.UpdateSuppressed,
            evaluation.SuppressionReason,
            evaluation.MateriallyChanged,
            Token(evaluation.TokenUsage),
            Cost(evaluation.EstimatedCost),
            EvaluationErrors(evaluation))
        {
            TicketSummary = Summary(evaluation.TicketSummary, evaluation.EngineeringSummary),
            AnalysisContext = context is null ? null : Context(context),
            AttachmentProcessing = evaluation.AttachmentProcessing.Select(item => Attachment(item, context)).ToArray(),
            Ai = new AiObservabilityResponse(
                evaluation.ProviderIdentifier, evaluation.ModelIdentifier, evaluation.ProviderReportedModel,
                evaluation.PromptVersion, evaluation.AiInteractions.Count, evaluation.AttachmentArtifactsReused,
                evaluation.AttachmentArtifactsRegenerated, evaluation.EvaluationReused,
                evaluation.OriginEvaluationId, evaluation.OriginRunId, evaluation.AnalysisExecutionMode)
            {
                ProviderRequestIds = evaluation.ProviderRequestIds
            },
            PlannedAdoMutation = evaluation.PlannedMutation is null ? null : new PlannedAdoMutationResponse(
                evaluation.PlannedMutation.PlanId, evaluation.PlannedMutation.EvaluatedRevision,
                evaluation.PlannedMutation.TagAdditions, evaluation.PlannedMutation.TagRemovals,
                evaluation.PlannedMutation.ExactCommentBody, evaluation.PlannedMutation.FutureDerivedAttachmentUploads,
                evaluation.PlannedMutation.ContentFingerprint),
            ReusableEvidenceAvailable = context is not null,
            ReusableEvidenceExpiresAtUtc = evaluation.ReusableEvidenceExpiresAtUtc
        };
    }

    private static StructuredTicketSummaryResponse Summary(StructuredTicketSummary summary, string? legacy) =>
        summary == StructuredTicketSummary.Empty && !string.IsNullOrWhiteSpace(legacy)
            ? StructuredTicketSummaryResponse.Empty with { IssueSummary = legacy }
            : new(summary.IssueSummary, summary.ExpectedBehavior, summary.ActualBehavior,
                summary.ReproductionSteps, summary.AffectedExamples, summary.Environment,
                summary.BusinessImpact, summary.AttachmentFindings, summary.InvestigationWarnings);

    private static AnalysisContextResponse Context(AnalysisContextSnapshot context) => new(
        context.SnapshotId, context.SchemaVersion, context.NormalizedEvidenceSchemaVersion,
        context.EvaluatedRevision, context.Evidence.Fields.Select(item => item.ReferenceName).ToArray(),
        context.Evidence.Processing.IncludedCommentCount,
        context.Evidence.Processing.AvailableHumanCommentCount,
        context.Evidence.Processing.ExcludedValidatorCommentCount,
        context.Evidence.Attachments.Count, context.Evidence.Processing.TruncationOccurred,
        context.Evidence.Redaction.RedactionOccurred, context.Evidence.Redaction.RedactionCount,
        context.ProcessingWarnings, context.ExpiresAtUtc);

    private static AttachmentProcessingResponse Attachment(
        AttachmentProcessingAuditRecord item, AnalysisContextSnapshot? context)
    {
        var evidence = context?.Evidence.Attachments.FirstOrDefault(candidate =>
            string.Equals(candidate.Reference, item.AttachmentId, StringComparison.Ordinal));
        var preview = evidence?.ExtractedEvidence ?? string.Empty;
        if (preview.Length > 2_000) preview = preview[..2_000] + "…";
        return new AttachmentProcessingResponse(item.AttachmentId, item.Name, item.MediaType,
            item.OriginalSize, item.ProcessingStatus, item.InspectionMode, item.ProcessorIdentity,
            item.ProcessorVersion, item.CacheReused, item.Truncated, item.Sampled,
            item.PagesAvailable, item.PagesInspected, item.FailureCategory, item.Warnings, preview);
    }

    private async Task<RuntimeConfigurationGeneration?> GenerationAsync(
        long? generationId,
        CancellationToken cancellationToken) => generationId is null
        ? null
        : await generations.GetAsync(generationId.Value, cancellationToken);

    private static long? GenerationId(OperationalRunAuditEnvelope envelope) =>
        envelope.IncrementalRun?.ConfigurationGenerationId ?? envelope.ItemRun?.ConfigurationGenerationId;

    private static OperationalRunStatus MapStatus(IncrementalRunStatus status) => status switch
    {
        IncrementalRunStatus.Running => OperationalRunStatus.Running,
        IncrementalRunStatus.Completed => OperationalRunStatus.Completed,
        IncrementalRunStatus.CompletedWithErrors => OperationalRunStatus.CompletedWithErrors,
        IncrementalRunStatus.Error => OperationalRunStatus.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static OperationalDecisionState Decision(EvaluationAuditRecord evaluation)
    {
        if (evaluation.Eligibility == WorkItemEligibility.NotEligible)
            return OperationalDecisionState.NotEligible;
        if (evaluation.ProcessingStatus == EvaluationProcessingStatus.Error)
            return OperationalDecisionState.Error;
        return evaluation.Decision switch
        {
            IntakeDecision.Pass => OperationalDecisionState.Pass,
            IntakeDecision.Fail => OperationalDecisionState.Fail,
            _ => OperationalDecisionState.Error
        };
    }

    public static string DecisionLabel(OperationalDecisionState decision) => decision switch
    {
        OperationalDecisionState.Pass => "Engineering Ready",
        OperationalDecisionState.Fail => "Intake Incomplete",
        OperationalDecisionState.Error => "Technical/System Failure",
        OperationalDecisionState.NotEligible => "Not Eligible",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static OperationalActorResponse Actor(AuditActor? actor) => actor switch
    {
        null => new(OperationalActorType.HistoricalUnknown, null, null),
        { Id: var id } when id == Guid.Empty => new(OperationalActorType.System, null, actor.Username),
        _ => new(OperationalActorType.User, actor.Id, actor.Username)
    };

    private static Uri? AdoUrl(EvaluationAuditRecord evaluation, RuntimeConfigurationGeneration? generation)
    {
        if (generation is null || !int.TryParse(evaluation.WorkItemId,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;
        var ado = generation.Configuration.Profile.Ado;
        return AzureDevOpsWorkItemResolver.CreateCanonicalUrl(ado.OrganizationUrl, ado.Project, id);
    }

    private static EffectResponse Effect(ProposedMutation mutation) => new(
        mutation.Type,
        mutation.Type == ProposedMutationType.PostComment ? "validatorComment" : "intakeTags",
        mutation.Tag);

    private static EffectResponse Effect(
        MutationOutcome outcome,
        IReadOnlyList<ProposedMutation> attempted) => new(
        outcome.Type,
        outcome.SafeTarget ?? (outcome.Type == ProposedMutationType.PostComment ? "validatorComment" : "intakeTags"),
        attempted.FirstOrDefault(mutation => mutation.Type == outcome.Type)?.Tag);

    private static MutationAttemptResponse Attempt(
        MutationOutcome outcome,
        IReadOnlyList<ProposedMutation> attempted) => new(
        outcome.Type,
        outcome.SafeTarget ?? (outcome.Type == ProposedMutationType.PostComment ? "validatorComment" : "intakeTags"),
        attempted.FirstOrDefault(mutation => mutation.Type == outcome.Type)?.Tag,
        outcome.Succeeded,
        outcome.AttemptedAtUtc,
        outcome.ProviderStatusCode,
        outcome.SafeErrorCategory);

    private static TokenUsageResponse? Token(TokenUsage? usage) => usage is null
        ? null
        : new TokenUsageResponse(usage.InputTokens, usage.OutputTokens, usage.TotalTokens);

    private static EstimatedCostResponse? Cost(EstimatedCost? cost) => cost is null
        ? null
        : new EstimatedCostResponse(
            cost.Amount,
            cost.Currency,
            cost.PricingIdentity,
            cost.Complete,
            cost.PricedInteractions,
            cost.TotalInteractions);

    private static IReadOnlyList<SafeOperationalErrorResponse> EvaluationErrors(EvaluationAuditRecord evaluation) =>
        evaluation.ErrorCategories
            .Concat(evaluation.MutationOutcomes.Where(outcome => !outcome.Succeeded)
                .Select(outcome => outcome.SafeErrorCategory).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Select(SafeError)
            .ToArray();

    private static SafeOperationalErrorResponse SafeError(string category) => new(category, category switch
    {
        var value when value.Contains("Credential", StringComparison.OrdinalIgnoreCase) =>
            "The configured credential revision is unavailable or invalid.",
        var value when value.Contains("AzureDevOps", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("WorkItem", StringComparison.OrdinalIgnoreCase) =>
            "Azure DevOps could not complete the requested operation.",
        var value when value.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("Transient", StringComparison.OrdinalIgnoreCase) =>
            "The evaluation provider could not complete the request.",
        var value when value.Contains("Persistence", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("Audit", StringComparison.OrdinalIgnoreCase) =>
            "Operational persistence could not complete safely.",
        var value when value.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("Parse", StringComparison.OrdinalIgnoreCase) =>
            "The evaluation response did not satisfy the required structured contract.",
        _ => "The operation did not complete successfully."
    });
}

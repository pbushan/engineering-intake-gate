using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.WorkItems;

public sealed record ManualWorkItemRunResult(
    int WorkItemId,
    string? EvaluatedRevision,
    WorkItemEligibility Eligibility,
    string? ExclusionReason,
    EvaluationProcessingStatus ProcessingStatus,
    IntakeDecision? Decision,
    ExecutionMode ExecutionMode,
    Guid RunId,
    string? EvaluationId,
    int ProposedMutationCount,
    string? ErrorCategory);

public sealed class ManualWorkItemRunService(
    IWorkItemSource workItemSource,
    IWorkItemEligibilityEvaluator eligibilityEvaluator,
    IntakeRunService intakeRunService,
    IAuditRepository auditRepository,
    IClock clock,
    IWorkItemReadLog log)
{
    public async Task<ManualWorkItemRunResult> ExecuteAsync(
        int workItemId,
        DeploymentConfiguration configuration,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(workItemId, configuration, AuditActor.System, cancellationToken);

    public async Task<ManualWorkItemRunResult> ExecuteAsync(
        int workItemId,
        DeploymentConfiguration configuration,
        AuditActor triggeredBy,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(workItemId, configuration, triggeredBy,
            AnalysisExecutionMode.NormalReuseEligible, cancellationToken);

    public async Task<ManualWorkItemRunResult> ExecuteAsync(
        int workItemId,
        DeploymentConfiguration configuration,
        AuditActor triggeredBy,
        AnalysisExecutionMode analysisExecutionMode,
        CancellationToken cancellationToken = default)
    {
        if (workItemId <= 0) throw new ArgumentOutOfRangeException(nameof(workItemId), "Work-item ID must be positive.");
        ArgumentNullException.ThrowIfNull(configuration);
        var runId = Guid.NewGuid();
        var query = await workItemSource.ExecuteSavedQueryAsync(cancellationToken);
        log.Discovery(runId, workItemId, query.Succeeded, query.WorkItemIds.Count, query.Failure?.SafeCategory);
        if (!query.Succeeded)
        {
            return await PersistSelectionAsync(workItemId, null, WorkItemEligibility.Unknown,
                "ConfiguredSavedQueryReadFailed", null, EvaluationProcessingStatus.Error,
                query.Failure!.SafeCategory, runId, configuration, triggeredBy, cancellationToken);
        }

        if (!query.WorkItemIds.Contains(workItemId))
        {
            log.Eligibility(runId, workItemId, WorkItemEligibility.NotEligible, "OutsideConfiguredSavedQuery");
            return await PersistSelectionAsync(workItemId, null, WorkItemEligibility.NotEligible,
                "OutsideConfiguredSavedQuery", null, EvaluationProcessingStatus.Completed,
                null, runId, configuration, triggeredBy, cancellationToken);
        }

        var read = await workItemSource.GetWorkItemAsync(workItemId, cancellationToken);
        if (!read.Succeeded)
        {
            log.ContentCollectionFailed(runId, workItemId, read.Failure!.SafeCategory);
            log.Eligibility(runId, workItemId, WorkItemEligibility.Eligible, "ConfiguredSavedQueryMember");
            return await PersistSelectionAsync(workItemId, null, WorkItemEligibility.Eligible,
                "ConfiguredSavedQueryMember", null, EvaluationProcessingStatus.Error,
                read.Failure!.SafeCategory, runId, configuration, triggeredBy, cancellationToken);
        }

        var workItem = read.WorkItem!;
        log.ContentCollection(runId, workItemId, workItem.Revision, workItem.Fields.Count,
            workItem.Comments.Count, workItem.Relations.Count, workItem.Attachments.Count);
        foreach (var rule in configuration.Profile.Exclusions)
        {
            if (!workItem.Fields.Any(field => string.Equals(field.ReferenceName, rule.Field, StringComparison.OrdinalIgnoreCase)))
                log.ExclusionFieldMissing(runId, workItemId, rule.Id, rule.Field);
        }
        var exclusion = eligibilityEvaluator.FindExclusion(workItem, configuration.Profile.Exclusions);
        if (exclusion is not null)
        {
            var safeReason = $"ExcludedByRule:{exclusion.RuleId}";
            log.Eligibility(runId, workItemId, WorkItemEligibility.NotEligible, safeReason);
            return await PersistSelectionAsync(workItemId, workItem.Revision, WorkItemEligibility.NotEligible,
                "ConfiguredExclusionMatched", safeReason, EvaluationProcessingStatus.Completed,
                null, runId, configuration, triggeredBy, cancellationToken);
        }

        log.Eligibility(runId, workItemId, WorkItemEligibility.Eligible, "ConfiguredSavedQueryMember");
        var result = await intakeRunService.ExecuteAsync(
            workItem,
            configuration,
            RunTriggerType.ManualWorkItem,
            "ConfiguredSavedQueryMember",
            runId,
            WorkItemEligibility.Eligible,
            configuration.Profile.Ado.SavedQueryId,
            null,
            triggeredBy,
            null,
            analysisExecutionMode,
            cancellationToken);

        return new ManualWorkItemRunResult(
            workItemId, workItem.Revision, WorkItemEligibility.Eligible, null,
            result.ProcessingStatus, result.Decision, configuration.Profile.Processing.ExecutionMode,
            result.RunId, result.EvaluationId, result.ProposedMutations.Count, result.ErrorCategory);
    }

    private async Task<ManualWorkItemRunResult> PersistSelectionAsync(
        int workItemId,
        string? revision,
        WorkItemEligibility eligibility,
        string selectionReason,
        string? exclusionReason,
        EvaluationProcessingStatus status,
        string? errorCategory,
        Guid runId,
        DeploymentConfiguration configuration,
        AuditActor triggeredBy,
        CancellationToken cancellationToken)
    {
        var evaluationId = Guid.NewGuid().ToString("D");
        var now = clock.UtcNow.ToUniversalTime();
        var isError = status == EvaluationProcessingStatus.Error;
        var run = new RunAuditRecord(
            runId, RunTriggerType.ManualWorkItem, now, now, configuration.Profile.Processing.ExecutionMode,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Version,
            configuration.PolicyFingerprint, status, 1, 0, 0, isError ? 1 : 0,
            eligibility == WorkItemEligibility.NotEligible ? 1 : 0, null, null, errorCategory)
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            SavedQueryId = configuration.Profile.Ado.SavedQueryId,
            Eligibility = eligibility,
            TriggeredBy = triggeredBy
        };
        var audit = new EvaluationAuditRecord(
            evaluationId, runId, workItemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            revision ?? string.Empty, now, selectionReason,
            configuration.Profile.Identity.Id, configuration.Policy.Identity.Id,
            configuration.Policy.Identity.Version, configuration.PolicyFingerprint,
            EvaluatorPrompt.Version, configuration.Profile.Ai.Provider, configuration.Profile.Ai.Model,
            [], [], [], [], null, false, 0, [], 0, 0, 0, 0, false, false,
            null, status, configuration.Profile.Processing.ExecutionMode, null, null, [], [], [],
            errorCategory is null ? [] : [errorCategory])
        {
            ConfigurationGenerationId = configuration.RuntimeGenerationId,
            SavedQueryId = configuration.Profile.Ado.SavedQueryId,
            Eligibility = eligibility,
            ExclusionReason = exclusionReason,
            TriggeredBy = triggeredBy
        };

        try
        {
            await auditRepository.SaveAsync(run, audit, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManualWorkItemRunResult(workItemId, revision, WorkItemEligibility.Unknown, null,
                EvaluationProcessingStatus.Error, null, configuration.Profile.Processing.ExecutionMode, runId, null, 0,
                "AuditPersistenceFailure");
        }

        return new ManualWorkItemRunResult(workItemId, revision, eligibility, exclusionReason,
            status, null, configuration.Profile.Processing.ExecutionMode, runId, evaluationId, 0, errorCategory);
    }
}

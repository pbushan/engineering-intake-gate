using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.WorkItems;

public interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemRetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Replays no evaluation.  It only completes the already persisted deterministic operations after
/// re-reading ADO state, using the original evaluation marker as the comment idempotency key.
/// </summary>
public sealed class MutationReconciliationService(
    IWorkItemSource source,
    IWorkItemWriter writer,
    IReconciliationRepository reconciliations,
    IAuditRepository audits,
    IClock clock,
    IRetryDelay delay)
{
    public async Task ReconcilePendingAsync(DeploymentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (configuration.Profile.Processing.ExecutionMode != ExecutionMode.Live) return;
        foreach (var record in await reconciliations.GetPendingAsync(configuration.Profile.Identity.Id, cancellationToken))
            await ReconcileOneAsync(record, configuration, cancellationToken);
    }

    private async Task ReconcileOneAsync(MutationReconciliationRecord record, DeploymentConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!int.TryParse(record.WorkItemId, out var workItemId))
        {
            await UpdateAsync(record, ReconciliationStatus.Error, "InvalidPersistedWorkItemId", cancellationToken);
            return;
        }
        var read = await source.GetWorkItemAsync(workItemId, cancellationToken);
        if (!read.Succeeded || read.WorkItem is null)
        {
            await UpdateAsync(record, ReconciliationStatus.Pending, read.Failure?.SafeCategory ?? "AzureDevOpsReadFailure", cancellationToken);
            return;
        }

        var current = read.WorkItem;
        var completed = record.CompletedMutationTypes.ToHashSet();
        var tagOperations = record.IntendedMutations.Where(item => item.Type is ProposedMutationType.AddTag or ProposedMutationType.RemoveTag).ToArray();
        var comment = record.IntendedMutations.SingleOrDefault(item => item.Type == ProposedMutationType.PostComment);
        if (TagStateSatisfied(current.Tags, tagOperations))
        {
            foreach (var operation in tagOperations) completed.Add(operation.Type);
        }
        if (comment is not null && current.Comments.Any(item => item.Content.Contains(comment.CommentMarker!, StringComparison.Ordinal)))
            completed.Add(ProposedMutationType.PostComment);

        if (AllDone(record.IntendedMutations, completed))
        {
            await CompleteAsync(record, completed, current.Revision, cancellationToken);
            return;
        }

        // A changed revision can only be accepted when the actual state proved every operation
        // complete above. Any missing write on changed evidence needs a fresh normal evaluation.
        if (!string.Equals(current.Revision, record.ExpectedRevision, StringComparison.Ordinal))
        {
            await UpdateAsync(record with { CompletedMutationTypes = completed.ToArray() }, ReconciliationStatus.StaleReevaluationRequired,
                "StaleRevisionReevaluationRequired", cancellationToken);
            return;
        }

        if (tagOperations.Length > 0 && !tagOperations.All(item => completed.Contains(item.Type)))
        {
            var preAttempt = record with { CompletedMutationTypes = completed.ToArray(), AttemptCount = record.AttemptCount + 1, UpdatedAtUtc = Now() };
            await reconciliations.UpdateAsync(preAttempt, cancellationToken); // persistence safety gate before a write
            var result = await RetryAsync(() => writer.UpdateIntakeTagsAsync(new IntakeTagUpdateRequest(workItemId, current.Revision, record.DesiredTags), cancellationToken), configuration.Profile.Processing.Retries, cancellationToken);
            await PersistOutcomeAsync(preAttempt, tagOperations, result, cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.ResultingRevision)) return;
            foreach (var operation in tagOperations) completed.Add(operation.Type);
            record = preAttempt with { CompletedMutationTypes = completed.ToArray(), ExpectedRevision = result.ResultingRevision, UpdatedAtUtc = Now(), FailureCategory = null };
            await reconciliations.UpdateAsync(record, cancellationToken);
        }

        if (comment is not null && !completed.Contains(ProposedMutationType.PostComment))
        {
            // The re-read is intentionally repeated after a tag write. A marker means an unknown
            // prior outcome is complete and must never produce a duplicate comment.
            var beforeComment = await source.GetWorkItemAsync(workItemId, cancellationToken);
            if (!beforeComment.Succeeded || beforeComment.WorkItem is null)
            {
                await UpdateAsync(record, ReconciliationStatus.Pending, beforeComment.Failure?.SafeCategory ?? "AzureDevOpsReadFailure", cancellationToken);
                return;
            }
            current = beforeComment.WorkItem;
            if (current.Comments.Any(item => item.Content.Contains(comment.CommentMarker!, StringComparison.Ordinal)))
            {
                completed.Add(ProposedMutationType.PostComment);
                await CompleteAsync(record with { CompletedMutationTypes = completed.ToArray() }, completed, current.Revision, cancellationToken);
                return;
            }
            if (!string.Equals(current.Revision, record.ExpectedRevision, StringComparison.Ordinal))
            {
                await UpdateAsync(record, ReconciliationStatus.StaleReevaluationRequired, "StaleRevisionReevaluationRequired", cancellationToken);
                return;
            }
            var preAttempt = record with { AttemptCount = record.AttemptCount + 1, UpdatedAtUtc = Now() };
            await reconciliations.UpdateAsync(preAttempt, cancellationToken);
            var result = await RetryAsync(() => writer.AddValidatorCommentAsync(new ValidatorCommentRequest(workItemId, current.Revision, comment.Body!, comment.CommentMarker!), cancellationToken), configuration.Profile.Processing.Retries, cancellationToken);
            await PersistOutcomeAsync(preAttempt, [comment], result, cancellationToken);
            if (!result.Succeeded) return;
            completed.Add(ProposedMutationType.PostComment);
        }
        await CompleteAsync(record, completed, current.Revision, cancellationToken);
    }

    private async Task PersistOutcomeAsync(MutationReconciliationRecord record, IReadOnlyList<ProposedMutation> operations, WorkItemMutationResult result, CancellationToken cancellationToken)
    {
        var existing = await audits.GetEvaluationAsync(record.EvaluationId, cancellationToken);
        var outcomes = existing?.MutationOutcomes.Concat(operations.Select(operation => new MutationOutcome(operation.Type, result.Succeeded, result.SafeErrorCategory)
        { AttemptedAtUtc = Now(), ProviderStatusCode = result.ProviderStatusCode, SafeTarget = operation.Type == ProposedMutationType.PostComment ? "validator-comment" : "intake-tags" })).ToArray() ?? [];
        var attempted = existing?.AttemptedMutations.Concat(operations).ToArray() ?? operations.ToArray();
        var state = result.Succeeded ? MutationExecutionState.Completed : result.FailureKind == WorkItemMutationFailureKind.Concurrency ? MutationExecutionState.StaleReevaluationRequired : MutationExecutionState.Failed;
        try
        {
            await audits.UpdateMutationAuditAsync(record.EvaluationId, attempted, outcomes, state, result.SafeErrorCategory is null ? [] : [result.SafeErrorCategory], cancellationToken);
            await UpdateAsync(record, ReconciliationStatus.Pending, result.SafeErrorCategory, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // The durable pre-attempt pending record remains.  The next run must inspect ADO,
            // rather than assuming the external request did or did not take effect.
        }
    }

    private async Task CompleteAsync(MutationReconciliationRecord record, HashSet<ProposedMutationType> completed, string revision, CancellationToken cancellationToken)
    {
        var finished = record with { CompletedMutationTypes = completed.Order().ToArray(), ExpectedRevision = revision, Status = ReconciliationStatus.Completed, UpdatedAtUtc = Now(), FailureCategory = null };
        await reconciliations.UpdateAsync(finished, cancellationToken);
        var audit = await audits.GetEvaluationAsync(record.EvaluationId, cancellationToken);
        if (audit is not null)
            await audits.UpdateMutationAuditAsync(record.EvaluationId, audit.AttemptedMutations, audit.MutationOutcomes, MutationExecutionState.Completed, [], cancellationToken);
    }

    private async Task UpdateAsync(MutationReconciliationRecord record, ReconciliationStatus status, string? category, CancellationToken cancellationToken) =>
        await reconciliations.UpdateAsync(record with { Status = status, UpdatedAtUtc = Now(), FailureCategory = category }, cancellationToken);

    private async Task<WorkItemMutationResult> RetryAsync(Func<Task<WorkItemMutationResult>> operation, int retries, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await operation();
            if (result.Succeeded || result.FailureKind != WorkItemMutationFailureKind.Transient || attempt >= retries) return result;
            await delay.DelayAsync(TimeSpan.FromMilliseconds(Math.Min(2_000, 200 * (attempt + 1))), cancellationToken);
        }
    }

    private static bool TagStateSatisfied(IReadOnlyCollection<string> tags, IReadOnlyList<ProposedMutation> operations) => operations.All(operation =>
        operation.Type == ProposedMutationType.AddTag ? tags.Contains(operation.Tag!, StringComparer.OrdinalIgnoreCase) :
        operation.Type == ProposedMutationType.RemoveTag ? !tags.Contains(operation.Tag!, StringComparer.OrdinalIgnoreCase) : true);
    private static bool AllDone(IReadOnlyList<ProposedMutation> intended, HashSet<ProposedMutationType> completed) => intended.All(item => completed.Contains(item.Type));
    private DateTimeOffset Now() => clock.UtcNow.ToUniversalTime();
}

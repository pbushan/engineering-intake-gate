using IntakeGate.Application.WorkItems;

namespace IntakeGate.Host.WorkItems;

public sealed class StructuredWorkItemReadLog(ILogger<StructuredWorkItemReadLog> logger) : IWorkItemReadLog
{
    public void Discovery(Guid runId, int? workItemId, bool succeeded, int resultCount, string? failureCategory) =>
        logger.LogInformation(
            "Work-item read stage completed. Event={EventName} Stage={Stage} RunId={RunId} WorkItemId={WorkItemId} Succeeded={Succeeded} ResultCount={ResultCount} FailureCategory={FailureCategory}",
            "WorkItemReadStage", "DISCOVERY", runId, workItemId, succeeded, resultCount, failureCategory);

    public void Eligibility(Guid runId, int workItemId, WorkItemEligibility eligibility, string? reason) =>
        logger.LogInformation(
            "Work-item read stage completed. Event={EventName} Stage={Stage} RunId={RunId} WorkItemId={WorkItemId} Eligibility={Eligibility} Reason={Reason}",
            "WorkItemReadStage", "ELIGIBILITY", runId, workItemId, eligibility, reason);

    public void ContentCollection(Guid runId, int workItemId, string revision, int fieldCount, int commentCount, int relationCount, int attachmentCount) =>
        logger.LogInformation(
            "Work-item read stage completed. Event={EventName} Stage={Stage} RunId={RunId} WorkItemId={WorkItemId} Revision={Revision} FieldCount={FieldCount} CommentCount={CommentCount} RelationCount={RelationCount} AttachmentCount={AttachmentCount}",
            "WorkItemReadStage", "CONTENT_COLLECTION", runId, workItemId, revision, fieldCount, commentCount, relationCount, attachmentCount);

    public void ContentCollectionFailed(Guid runId, int workItemId, string failureCategory) =>
        logger.LogWarning(
            "Work-item read stage failed. Event={EventName} Stage={Stage} RunId={RunId} WorkItemId={WorkItemId} FailureCategory={FailureCategory}",
            "WorkItemReadStage", "CONTENT_COLLECTION", runId, workItemId, failureCategory);

    public void ExclusionFieldMissing(Guid runId, int workItemId, string ruleId, string fieldReference) =>
        logger.LogInformation(
            "Configured exclusion field was absent and did not match. Event={EventName} Stage={Stage} RunId={RunId} WorkItemId={WorkItemId} RuleId={RuleId} FieldReference={FieldReference}",
            "ExclusionFieldMissing", "ELIGIBILITY", runId, workItemId, ruleId, fieldReference);

    public void Retry(string operation, int? workItemId, int attempt, string failureCategory) =>
        logger.LogWarning(
            "Azure DevOps read retry. Event={EventName} Operation={Operation} WorkItemId={WorkItemId} Attempt={Attempt} FailureCategory={FailureCategory}",
            "AzureDevOpsReadRetry", operation, workItemId, attempt, failureCategory);
}

using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Host.Audit;

public sealed class StructuredRunAuditLog(ILogger<StructuredRunAuditLog> logger) : IRunAuditLog
{
    public void Decision(Guid runId, string evaluationId, IntakeDecision? decision, EvaluationProcessingStatus status, ExecutionMode mode, int proposedMutationCount) =>
        logger.LogInformation(
            "Decision mapped. Event={EventName} Stage={Stage} RunId={RunId} EvaluationId={EvaluationId} Decision={Decision} ProcessingStatus={ProcessingStatus} ExecutionMode={ExecutionMode} ProposedMutationCount={ProposedMutationCount}",
            "DecisionMapped", "DECISION", runId, evaluationId, decision, status, mode, proposedMutationCount);

    public void Audit(Guid runId, string evaluationId, bool succeeded, string? failureCategory) =>
        logger.LogInformation(
            "Audit persistence completed. Event={EventName} Stage={Stage} RunId={RunId} EvaluationId={EvaluationId} AuditSucceeded={AuditSucceeded} FailureCategory={FailureCategory}",
            "AuditPersisted", "AUDIT", runId, evaluationId, succeeded, failureCategory);

    public void EvaluationCache(Guid runId, string evaluationId, bool succeeded, string? failureCategory) =>
        logger.LogInformation(
            "Evaluation cache persistence completed. Event={EventName} Stage={Stage} RunId={RunId} EvaluationId={EvaluationId} CacheSucceeded={CacheSucceeded} FailureCategory={FailureCategory}",
            "EvaluationCachePersisted", "CACHE", runId, evaluationId, succeeded, failureCategory);
}

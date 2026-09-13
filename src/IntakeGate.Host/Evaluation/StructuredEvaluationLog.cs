using IntakeGate.Application.Evaluation;

namespace IntakeGate.Host.Evaluation;

public sealed class StructuredEvaluationLog(ILogger<StructuredEvaluationLog> logger) : IEvaluationLog
{
    public void EvaluationAttempt(EvaluationLogEntry entry) => logger.LogInformation(
        "AI evaluation processed. Event={EventName} Stage={Stage} EvaluationId={EvaluationId} Provider={Provider} ConfiguredModel={ConfiguredModel} ProviderReportedModel={ProviderReportedModel} PromptVersion={PromptVersion} PolicyFingerprint={PolicyFingerprint} Attempt={Attempt} ProviderRequestId={ProviderRequestId} LatencyMilliseconds={LatencyMilliseconds} InputTokens={InputTokens} OutputTokens={OutputTokens} TotalTokens={TotalTokens} ProcessingStatus={ProcessingStatus} Decision={Decision} FailureCategory={FailureCategory} WillRetry={WillRetry}",
        "AiEvaluation", "AI_EVALUATION", entry.EvaluationId, entry.ProviderIdentifier, entry.ModelIdentifier,
        entry.ProviderReportedModel, entry.PromptVersion, entry.PolicyFingerprint, entry.Attempt, entry.ProviderRequestId, entry.LatencyMilliseconds,
        entry.TokenUsage?.InputTokens, entry.TokenUsage?.OutputTokens, entry.TokenUsage?.TotalTokens, entry.ProcessingStatus, entry.Decision,
        entry.FailureCategory, entry.WillRetry);
}

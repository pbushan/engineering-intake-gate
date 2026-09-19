using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Audit;
using System.Diagnostics;

namespace IntakeGate.Application.Evaluation;

public sealed class IntakeEvaluationService(
    IEvaluationRequestBuilder requestBuilder,
    IIntakeAiProvider provider,
    EvaluationResponseParser responseParser,
    EvaluationContractValidator contractValidator,
    IEvaluationLog evaluationLog) : IIntakeEvaluationService
{
    public async Task<EvaluationProcessingResult> EvaluateAsync(
        EvaluationEvidence evidence,
        DeploymentConfiguration configuration,
        CancellationToken cancellationToken = default)
        => await EvaluateCoreAsync(evidence, configuration, null, cancellationToken);

    public async Task<EvaluationProcessingResult> EvaluateAsync(
        EvaluationEvidence evidence,
        DeploymentConfiguration configuration,
        string evaluationId,
        CancellationToken cancellationToken = default)
        => await EvaluateCoreAsync(evidence, configuration, evaluationId, cancellationToken);

    private async Task<EvaluationProcessingResult> EvaluateCoreAsync(
        EvaluationEvidence evidence,
        DeploymentConfiguration configuration,
        string? evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(configuration);
        var request = requestBuilder.Build(evidence, configuration, evaluationId);
        var maxAttempts = configuration.Profile.Processing.Retries + 1;
        var inputTokens = 0;
        var outputTokens = 0;
        var totalTokens = 0;
        var hasUsage = false;
        string? reportedModel = null;
        var requestIds = new List<string>();
        var interactions = new List<AiProviderInteractionUsage>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            AiProviderResponse response;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                response = await provider.EvaluateAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response = AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Transient, "ProviderTimeout"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                response = AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Permanent, "ProviderUnhandledFailure"));
            }
            stopwatch.Stop();

            if (response.Metadata?.TokenUsage is { } usage)
            {
                hasUsage = true;
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;
                totalTokens += usage.TotalTokens;
            }
            reportedModel = response.Metadata?.ProviderReportedModel ?? reportedModel;
            if (!string.IsNullOrWhiteSpace(response.Metadata?.ProviderRequestId)) requestIds.Add(response.Metadata.ProviderRequestId);
            interactions.Add(new AiProviderInteractionUsage(
                attempt,
                request.ProviderIdentifier,
                request.ModelIdentifier,
                request.ProviderIdentifier,
                response.Metadata?.ProviderReportedModel,
                response.Metadata?.ProviderRequestId,
                response.Metadata?.TokenUsage));
            var aggregateUsage = hasUsage ? new TokenUsage(inputTokens, outputTokens, totalTokens) : null;

            if (response.Failure is not null)
            {
                var retry = response.Failure.Kind == AiProviderFailureKind.Transient && attempt < maxAttempts;
                evaluationLog.EvaluationAttempt(new EvaluationLogEntry(request.EvaluationId, request.ProviderIdentifier, request.ModelIdentifier,
                    request.PromptVersion, request.PolicyFingerprint, attempt, EvaluationProcessingStatus.Error, null,
                    response.Failure.SafeCategory, retry)
                {
                    ProviderReportedModel = response.Metadata?.ProviderReportedModel,
                    ProviderRequestId = response.Metadata?.ProviderRequestId,
                    TokenUsage = response.Metadata?.TokenUsage,
                    LatencyMilliseconds = stopwatch.ElapsedMilliseconds
                });
                if (retry) continue;
                return WithMetadata(new EvaluationProcessingResult(EvaluationProcessingStatus.Error, null, new EvaluationFailure(response.Failure.SafeCategory), attempt), aggregateUsage, reportedModel, requestIds, interactions);
            }

            if (!responseParser.TryParseResponse(response.StructuredPayload, out var untrusted, out var parseFailure))
            {
                var retry = attempt < maxAttempts;
                evaluationLog.EvaluationAttempt(new EvaluationLogEntry(request.EvaluationId, request.ProviderIdentifier, request.ModelIdentifier,
                    request.PromptVersion, request.PolicyFingerprint, attempt, EvaluationProcessingStatus.Error, null, parseFailure, retry)
                {
                    ProviderReportedModel = response.Metadata?.ProviderReportedModel,
                    ProviderRequestId = response.Metadata?.ProviderRequestId,
                    TokenUsage = response.Metadata?.TokenUsage,
                    LatencyMilliseconds = stopwatch.ElapsedMilliseconds
                });
                if (retry) continue;
                return WithMetadata(new EvaluationProcessingResult(EvaluationProcessingStatus.Error, null, new EvaluationFailure(parseFailure), attempt), aggregateUsage, reportedModel, requestIds, interactions);
            }
            if (!contractValidator.TryValidate(untrusted!, request, out var trusted, out var validationFailure))
            {
                var retry = attempt < maxAttempts;
                evaluationLog.EvaluationAttempt(new EvaluationLogEntry(request.EvaluationId, request.ProviderIdentifier, request.ModelIdentifier,
                    request.PromptVersion, request.PolicyFingerprint, attempt, EvaluationProcessingStatus.Error, null, validationFailure, retry)
                {
                    ProviderReportedModel = response.Metadata?.ProviderReportedModel,
                    ProviderRequestId = response.Metadata?.ProviderRequestId,
                    TokenUsage = response.Metadata?.TokenUsage,
                    LatencyMilliseconds = stopwatch.ElapsedMilliseconds
                });
                if (retry) continue;
                return WithMetadata(new EvaluationProcessingResult(EvaluationProcessingStatus.Error, null, new EvaluationFailure(validationFailure), attempt), aggregateUsage, reportedModel, requestIds, interactions);
            }

            evaluationLog.EvaluationAttempt(new EvaluationLogEntry(request.EvaluationId, request.ProviderIdentifier, request.ModelIdentifier,
                request.PromptVersion, request.PolicyFingerprint, attempt, EvaluationProcessingStatus.Completed, trusted!.Decision, null, false)
            {
                ProviderReportedModel = response.Metadata?.ProviderReportedModel,
                ProviderRequestId = response.Metadata?.ProviderRequestId,
                TokenUsage = response.Metadata?.TokenUsage,
                LatencyMilliseconds = stopwatch.ElapsedMilliseconds
            });
            return WithMetadata(new EvaluationProcessingResult(EvaluationProcessingStatus.Completed, trusted, null, attempt), aggregateUsage, reportedModel, requestIds, interactions);
        }

        throw new InvalidOperationException("Evaluation attempt bounds must produce a result.");
    }

    private static EvaluationProcessingResult WithMetadata(
        EvaluationProcessingResult result,
        TokenUsage? usage,
        string? reportedModel,
        IReadOnlyList<string> requestIds,
        IReadOnlyList<AiProviderInteractionUsage> interactions) => result with
        {
            TokenUsage = usage,
            ProviderReportedModel = reportedModel,
            ProviderRequestIds = requestIds.ToArray(),
            ProviderInteractions = interactions.ToArray()
        };
}

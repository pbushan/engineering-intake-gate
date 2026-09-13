using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.Evaluation;

public interface IEvaluationRequestBuilder
{
    EvaluationRequest Build(EvaluationEvidence evidence, DeploymentConfiguration configuration, string? evaluationId = null);
}

public sealed class EvaluationRequestBuilder : IEvaluationRequestBuilder
{
    public EvaluationRequest Build(EvaluationEvidence evidence, DeploymentConfiguration configuration, string? evaluationId = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(configuration);

        var id = evaluationId ?? Guid.NewGuid().ToString("D");
        if (!Guid.TryParseExact(id, "D", out _) || !string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Evaluation IDs must be canonical lowercase UUIDs.", nameof(evaluationId));
        }

        return new EvaluationRequest(
            id,
            EvaluatorPrompt.Version,
            EvaluatorPrompt.Content,
            evidence,
            configuration.Policy.Identity.Id,
            configuration.Policy.Identity.Version,
            configuration.PolicyFingerprint,
            new EvaluationProfileContext(configuration.Profile.Identity.Id, configuration.Profile.Identity.Version),
            configuration.Policy.Criteria.Select(criterion => new EvaluationCriterion(
                criterion.Id,
                criterion.DisplayName,
                criterion.Description,
                criterion.Applicability,
                criterion.NotApplicable,
                criterion.EvaluationGuidance)).ToArray(),
            configuration.Profile.Ai.Provider,
            configuration.Profile.Ai.Model)
        {
            VisualEvidence = evidence.VisualEvidence
        };
    }
}

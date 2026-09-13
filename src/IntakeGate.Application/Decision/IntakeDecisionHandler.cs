using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Application.Decision;

public interface IIntakeDecisionHandler
{
    DecisionResult Decide(
        EvaluationProcessingResult evaluation,
        IReadOnlyCollection<string> currentTags,
        IntakeStateConfiguration intakeState,
        Uri policyUrl);
}

public sealed record DecisionResult(IReadOnlyList<ProposedMutation> ProposedMutations, string? RenderedCommentBody);

public sealed class IntakeDecisionHandler(IIntakeCommentRenderer commentRenderer) : IIntakeDecisionHandler
{
    public DecisionResult Decide(
        EvaluationProcessingResult evaluation,
        IReadOnlyCollection<string> currentTags,
        IntakeStateConfiguration intakeState,
        Uri policyUrl)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(currentTags);
        ArgumentNullException.ThrowIfNull(intakeState);
        ArgumentNullException.ThrowIfNull(policyUrl);

        if (evaluation.ProcessingStatus != EvaluationProcessingStatus.Completed || evaluation.Result is null)
        {
            return new DecisionResult([], null);
        }

        var tags = currentTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = evaluation.Result;
        var mutations = new List<ProposedMutation>(3);

        // Stable order is removal, addition, comment.
        if (result.Decision == IntakeDecision.Pass)
        {
            if (tags.Contains(intakeState.IncompleteTag)) mutations.Add(ProposedMutation.RemoveTag(intakeState.IncompleteTag));
            if (!tags.Contains(intakeState.ValidatedTag)) mutations.Add(ProposedMutation.AddTag(intakeState.ValidatedTag));
        }
        else
        {
            if (tags.Contains(intakeState.ValidatedTag)) mutations.Add(ProposedMutation.RemoveTag(intakeState.ValidatedTag));
            if (!tags.Contains(intakeState.IncompleteTag)) mutations.Add(ProposedMutation.AddTag(intakeState.IncompleteTag));
        }

        var comment = commentRenderer.Render(result, policyUrl);
        mutations.Add(ProposedMutation.PostComment(comment.Body, comment.Marker));
        return new DecisionResult(mutations, comment.Body);
    }
}

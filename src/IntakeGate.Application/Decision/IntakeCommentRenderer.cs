using System.Text;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.Decision;

public interface IIntakeCommentRenderer
{
    RenderedComment Render(EvaluationResult result, Uri policyUrl);
}

public sealed record RenderedComment(string Body, string Marker);

public sealed class IntakeCommentRenderer : IIntakeCommentRenderer
{
    private const string AiDisclosure = "This ticket was reviewed using AI and this comment was AI-generated.";

    public RenderedComment Render(EvaluationResult result, Uri policyUrl)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policyUrl);

        var marker = ValidatorCommentMarker.Create(Guid.ParseExact(result.EvaluationId, "D"));
        return result.Decision switch
        {
            IntakeDecision.Pass => new RenderedComment(RenderPass(result, marker), marker),
            IntakeDecision.Fail => new RenderedComment(RenderFail(result, policyUrl, marker), marker),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }

    private static string RenderPass(EvaluationResult result, string marker) => $"""
        ✅ Engineering intake validated

        This ticket contains sufficient information for Engineering to begin investigation without avoidable clarification.

        Engineering Intake Summary

        {result.EngineeringSummary}

        This intake validation does not confirm defect classification, root cause, severity, priority, solution, or ownership.

        {AiDisclosure}

        {marker}
        """;

    private static string RenderFail(EvaluationResult result, Uri policyUrl, string marker)
    {
        var builder = new StringBuilder();
        builder.AppendLine("⚠️ Engineering intake incomplete");
        builder.AppendLine();
        builder.AppendLine("This ticket does not yet contain enough information for Engineering to begin investigation without avoidable clarification.");

        if (result.Deficiencies.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Missing or insufficient information");
            foreach (var deficiency in result.Deficiencies)
            {
                builder.AppendLine();
                builder.Append("- ").Append(deficiency.CriterionId).Append(": ").AppendLine(deficiency.Reason);
                builder.Append("  Support action: ").AppendLine(deficiency.RequiredSupportAction);
            }
        }

        if (result.Ambiguities.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Clarification required");
            foreach (var ambiguity in result.Ambiguities)
            {
                builder.AppendLine();
                builder.Append("- ").AppendLine(ambiguity.Description);
                builder.Append("  Support action: ").AppendLine(ambiguity.RequiredClarification);
            }
        }

        builder.AppendLine();
        builder.Append("Intake policy: ").AppendLine(policyUrl.AbsoluteUri);
        builder.AppendLine();
        builder.AppendLine("This assessment concerns intake completeness only and does not classify the ticket as a defect or enhancement.");
        builder.AppendLine();
        builder.AppendLine(AiDisclosure);
        builder.AppendLine();
        builder.Append(marker);
        return builder.ToString();
    }
}

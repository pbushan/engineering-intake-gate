namespace IntakeGate.Application.Decision;

using System.Security.Cryptography;
using System.Text;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;

public enum ProposedMutationType
{
    RemoveTag,
    AddTag,
    PostComment
}

/// <summary>The complete mutation vocabulary. It represents intent, never an attempted write.</summary>
public sealed record ProposedMutation
{
    public ProposedMutation(ProposedMutationType type, string? tag, string? body, string? commentMarker)
    {
        if (type is ProposedMutationType.AddTag or ProposedMutationType.RemoveTag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            if (body is not null || commentMarker is not null)
            {
                throw new ArgumentException("Tag mutations cannot contain comment data.");
            }
        }
        else if (type == ProposedMutationType.PostComment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body);
            ArgumentException.ThrowIfNullOrWhiteSpace(commentMarker);
            if (tag is not null)
            {
                throw new ArgumentException("Comment mutations cannot contain a tag.");
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        Tag = tag;
        Body = body;
        CommentMarker = commentMarker;
    }

    public ProposedMutationType Type { get; }
    public string? Tag { get; }
    public string? Body { get; }
    public string? CommentMarker { get; }

    public static ProposedMutation AddTag(string tag) => new(ProposedMutationType.AddTag, tag, null, null);
    public static ProposedMutation RemoveTag(string tag) => new(ProposedMutationType.RemoveTag, tag, null, null);
    public static ProposedMutation PostComment(string body, string marker) => new(ProposedMutationType.PostComment, null, body, marker);
}

public sealed record PlannedAdoMutation(
    string PlanId,
    string EvaluatedRevision,
    IReadOnlyList<string> TagAdditions,
    IReadOnlyList<string> TagRemovals,
    string? ExactCommentBody,
    IReadOnlyList<string> FutureDerivedAttachmentUploads,
    string ContentFingerprint,
    string EvaluationId,
    string AnalysisContextId)
{
    public static PlannedAdoMutation Create(
        string evaluatedRevision,
        IReadOnlyList<ProposedMutation> mutations,
        EvaluationResult result,
        string analysisContextId)
    {
        var additions = mutations.Where(item => item.Type == ProposedMutationType.AddTag).Select(item => item.Tag!).Order(StringComparer.Ordinal).ToArray();
        var removals = mutations.Where(item => item.Type == ProposedMutationType.RemoveTag).Select(item => item.Tag!).Order(StringComparer.Ordinal).ToArray();
        var body = mutations.SingleOrDefault(item => item.Type == ProposedMutationType.PostComment)?.Body;
        var canonical = string.Join('\n', new[]
        {
            result.Decision.ToString(), result.TicketSummary.IssueSummary ?? string.Empty,
            result.TicketSummary.ExpectedBehavior ?? string.Empty, result.TicketSummary.ActualBehavior ?? string.Empty,
            string.Join('|', result.TicketSummary.ReproductionSteps), string.Join('|', result.TicketSummary.AttachmentFindings),
            string.Join('|', result.TicketSummary.InvestigationWarnings),
            string.Join('|', result.Deficiencies.Select(item => $"{item.CriterionId}:{item.Reason}:{item.RequiredSupportAction}")),
            string.Join('|', result.Ambiguities.Select(item => $"{item.CriterionId}:{item.Description}:{item.RequiredClarification}")),
            string.Join('|', additions), string.Join('|', removals), ValidatorCommentMarker.Strip(body)
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new PlannedAdoMutation(Guid.NewGuid().ToString("D"), evaluatedRevision, additions, removals,
            body, [], fingerprint, result.EvaluationId, analysisContextId);
    }
}

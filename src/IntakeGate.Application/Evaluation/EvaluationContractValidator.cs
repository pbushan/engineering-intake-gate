using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.Evaluation;

public sealed class EvaluationContractValidator
{
    public bool TryValidate(
        UntrustedEvaluationResponse response,
        EvaluationRequest request,
        out EvaluationResult? result,
        out string failureCategory)
    {
        result = null;
        failureCategory = "InvalidEvaluationContract";
        if (!string.Equals(response.SchemaVersion, AnalysisContextVersions.EvaluationContract, StringComparison.Ordinal))
        {
            failureCategory = "UnsupportedSchemaVersion";
            return false;
        }
        if (!IsCanonicalUuid(response.EvaluationId) || !string.Equals(response.EvaluationId, request.EvaluationId, StringComparison.Ordinal))
        {
            failureCategory = "InvalidEvaluationId";
            return false;
        }
        if (!TryDecision(response.Decision, out var decision))
        {
            failureCategory = "UnknownDecision";
            return false;
        }

        var known = request.Criteria.Select(criterion => criterion.Id).ToHashSet(StringComparer.Ordinal);
        if (!AllKnown(response.ApplicableCriteria, known) || !AllKnown(response.SatisfiedCriteria, known) ||
            !response.Deficiencies.All(item => known.Contains(item.CriterionId)) ||
            !response.Ambiguities.All(item => item.CriterionId is null || known.Contains(item.CriterionId)))
        {
            failureCategory = "UnknownCriterionId";
            return false;
        }
        if (HasDuplicates(response.ApplicableCriteria) || HasDuplicates(response.SatisfiedCriteria) ||
            HasDuplicates(response.Deficiencies.Select(item => item.CriterionId)) ||
            HasDuplicates(response.Ambiguities.Where(item => item.CriterionId is not null).Select(item => item.CriterionId!)))
        {
            failureCategory = "DuplicateCriterionId";
            return false;
        }

        var applicable = response.ApplicableCriteria.ToHashSet(StringComparer.Ordinal);
        var satisfied = response.SatisfiedCriteria.ToHashSet(StringComparer.Ordinal);
        var deficient = response.Deficiencies.Select(item => item.CriterionId).ToHashSet(StringComparer.Ordinal);
        if (!satisfied.IsSubsetOf(applicable) || !deficient.IsSubsetOf(applicable) || satisfied.Overlaps(deficient))
        {
            failureCategory = "InconsistentCriterionClassification";
            return false;
        }
        var ambiguityCriteria = response.Ambiguities.Where(item => item.CriterionId is not null).Select(item => item.CriterionId!).ToHashSet(StringComparer.Ordinal);
        if (!applicable.All(id => satisfied.Contains(id) || deficient.Contains(id) || ambiguityCriteria.Contains(id)))
        {
            failureCategory = "UnclassifiedApplicableCriterion";
            return false;
        }
        if (decision == IntakeDecision.Pass && (response.Deficiencies.Count > 0 || response.Ambiguities.Count > 0))
        {
            failureCategory = "PassContainsGap";
            return false;
        }
        if (decision == IntakeDecision.Fail && response.Deficiencies.Count == 0 && response.Ambiguities.Count == 0)
        {
            failureCategory = "FailWithoutGap";
            return false;
        }

        result = new EvaluationResult(
            response.EvaluationId, decision, request.PolicyId, request.PolicyVersion, request.PolicyFingerprint,
            request.PromptVersion, response.ApplicableCriteria, response.SatisfiedCriteria,
            response.Deficiencies.Select(item => new EvaluationDeficiency(item.CriterionId, item.Reason, item.RequiredSupportAction)).ToArray(),
            response.Ambiguities.Select(item => new EvaluationAmbiguity(item.CriterionId, item.Description, item.RequiredClarification)).ToArray(),
            response.EngineeringSummary, request.ProviderIdentifier, request.ModelIdentifier)
        {
            TicketSummary = new StructuredTicketSummary(
                response.TicketSummary.IssueSummary, response.TicketSummary.ExpectedBehavior,
                response.TicketSummary.ActualBehavior, response.TicketSummary.ReproductionSteps,
                response.TicketSummary.AffectedExamples, response.TicketSummary.Environment,
                response.TicketSummary.BusinessImpact, response.TicketSummary.AttachmentFindings,
                response.TicketSummary.InvestigationWarnings)
        };
        return true;
    }

    private static bool IsCanonicalUuid(string value) => Guid.TryParseExact(value, "D", out _) && value == value.ToLowerInvariant();
    private static bool TryDecision(string value, out IntakeDecision decision)
    {
        if (string.Equals(value, "PASS", StringComparison.Ordinal)) { decision = IntakeDecision.Pass; return true; }
        if (string.Equals(value, "FAIL", StringComparison.Ordinal)) { decision = IntakeDecision.Fail; return true; }
        decision = default;
        return false;
    }
    private static bool AllKnown(IEnumerable<string> ids, ISet<string> known) => ids.All(known.Contains);
    private static bool HasDuplicates(IEnumerable<string> ids) => ids.GroupBy(item => item, StringComparer.Ordinal).Any(group => group.Count() > 1);
}

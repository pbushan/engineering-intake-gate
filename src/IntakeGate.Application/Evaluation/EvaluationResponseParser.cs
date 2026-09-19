using System.Text.Json;

namespace IntakeGate.Application.Evaluation;

public sealed record UntrustedEvaluationResponse(
    string SchemaVersion,
    string EvaluationId,
    string Decision,
    IReadOnlyList<string> ApplicableCriteria,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<UntrustedEvaluationDeficiency> Deficiencies,
    IReadOnlyList<UntrustedEvaluationAmbiguity> Ambiguities,
    string EngineeringSummary)
{
    public UntrustedStructuredTicketSummary TicketSummary { get; init; } = UntrustedStructuredTicketSummary.Empty;
}

public sealed record UntrustedStructuredTicketSummary(
    string? IssueSummary,
    string? ExpectedBehavior,
    string? ActualBehavior,
    IReadOnlyList<string> ReproductionSteps,
    IReadOnlyList<string> AffectedExamples,
    string? Environment,
    string? BusinessImpact,
    IReadOnlyList<string> AttachmentFindings,
    IReadOnlyList<string> InvestigationWarnings)
{
    public static UntrustedStructuredTicketSummary Empty { get; } = new(null, null, null, [], [], null, null, [], []);
}

public sealed record UntrustedEvaluationDeficiency(string CriterionId, string Reason, string RequiredSupportAction);
public sealed record UntrustedEvaluationAmbiguity(string? CriterionId, string Description, string RequiredClarification);

public sealed class EvaluationResponseParser
{
    private static readonly HashSet<string> TopLevelProperties =
        ["schemaVersion", "evaluationId", "decision", "applicableCriteria", "satisfiedCriteria", "deficiencies", "ambiguities", "engineeringSummary", "ticketSummary"];
    private static readonly HashSet<string> SummaryProperties =
        ["issueSummary", "expectedBehavior", "actualBehavior", "reproductionSteps", "affectedExamples", "environment", "businessImpact", "attachmentFindings", "investigationWarnings"];
    private static readonly HashSet<string> DeficiencyProperties = ["criterionId", "reason", "requiredSupportAction"];
    private static readonly HashSet<string> AmbiguityProperties = ["criterionId", "description", "requiredClarification"];

    public bool TryParseResponse(string? payload, out UntrustedEvaluationResponse? response, out string failureCategory)
    {
        response = null;
        failureCategory = "MalformedStructuredResponse";
        if (string.IsNullOrWhiteSpace(payload)) return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactlyAllowedProperties(root, TopLevelProperties)) return false;

            if (!TryString(root, "schemaVersion", out var schemaVersion) ||
                !TryString(root, "evaluationId", out var evaluationId) ||
                !TryString(root, "decision", out var decision) ||
                !TryString(root, "engineeringSummary", out var summary) ||
                !TryStringArray(root, "applicableCriteria", out var applicable) ||
                !TryStringArray(root, "satisfiedCriteria", out var satisfied) ||
                !TryDeficiencies(root, out var deficiencies) ||
                !TryAmbiguities(root, out var ambiguities)) return false;

            if (!root.TryGetProperty("ticketSummary", out var summaryNode) || !TrySummary(summaryNode, out var ticketSummary)) return false;
            response = new UntrustedEvaluationResponse(schemaVersion, evaluationId, decision, applicable, satisfied, deficiencies, ambiguities, summary)
            {
                TicketSummary = ticketSummary
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TrySummary(JsonElement node, out UntrustedStructuredTicketSummary summary)
    {
        summary = UntrustedStructuredTicketSummary.Empty;
        if (node.ValueKind != JsonValueKind.Object || !HasExactlyAllowedProperties(node, SummaryProperties) ||
            !TryOptionalString(node, "issueSummary", out var issue) ||
            !TryOptionalString(node, "expectedBehavior", out var expected) ||
            !TryOptionalString(node, "actualBehavior", out var actual) ||
            !TryStringArray(node, "reproductionSteps", out var steps, allowEmpty: true) ||
            !TryStringArray(node, "affectedExamples", out var examples, allowEmpty: true) ||
            !TryOptionalString(node, "environment", out var environment) ||
            !TryOptionalString(node, "businessImpact", out var impact) ||
            !TryStringArray(node, "attachmentFindings", out var findings, allowEmpty: true) ||
            !TryStringArray(node, "investigationWarnings", out var warnings, allowEmpty: true)) return false;
        summary = new(issue, expected, actual, steps, examples, environment, impact, findings, warnings);
        return true;
    }

    private static bool TryDeficiencies(JsonElement root, out IReadOnlyList<UntrustedEvaluationDeficiency> items)
    {
        items = Array.Empty<UntrustedEvaluationDeficiency>();
        if (!root.TryGetProperty("deficiencies", out var array) || array.ValueKind != JsonValueKind.Array) return false;
        var result = new List<UntrustedEvaluationDeficiency>();
        foreach (var item in array.EnumerateArray())
        {
            if (result.Count >= 50) return false;
            if (item.ValueKind != JsonValueKind.Object || !HasExactlyAllowedProperties(item, DeficiencyProperties) ||
                !TryString(item, "criterionId", out var id) || !TryString(item, "reason", out var reason) ||
                !TryString(item, "requiredSupportAction", out var action)) return false;
            result.Add(new UntrustedEvaluationDeficiency(id, reason, action));
        }
        items = result;
        return true;
    }

    private static bool TryAmbiguities(JsonElement root, out IReadOnlyList<UntrustedEvaluationAmbiguity> items)
    {
        items = Array.Empty<UntrustedEvaluationAmbiguity>();
        if (!root.TryGetProperty("ambiguities", out var array) || array.ValueKind != JsonValueKind.Array) return false;
        var result = new List<UntrustedEvaluationAmbiguity>();
        foreach (var item in array.EnumerateArray())
        {
            if (result.Count >= 50) return false;
            if (item.ValueKind != JsonValueKind.Object || !HasExactlyAllowedProperties(item, AmbiguityProperties) ||
                !TryOptionalString(item, "criterionId", out var id) || !TryString(item, "description", out var description) ||
                !TryString(item, "requiredClarification", out var clarification)) return false;
            result.Add(new UntrustedEvaluationAmbiguity(id, description, clarification));
        }
        items = result;
        return true;
    }

    private static bool TryStringArray(JsonElement root, string property, out IReadOnlyList<string> items, bool allowEmpty = false)
    {
        items = Array.Empty<string>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return false;
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (result.Count >= 50) return false;
            if (item.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(item.GetString())) ||
                item.GetString() is { Length: > 4000 }) return false;
            if (string.IsNullOrWhiteSpace(item.GetString())) return false;
            result.Add(item.GetString()!);
        }
        items = result;
        return true;
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String) return false;
        var content = node.GetString();
        if (string.IsNullOrWhiteSpace(content) || content.Length > 8000) return false;
        value = content;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Null) return true;
        if (node.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetString()) || node.GetString()!.Length > 4000) return false;
        value = node.GetString()!;
        return true;
    }

    private static bool HasExactlyAllowedProperties(JsonElement element, HashSet<string> allowed) =>
        element.EnumerateObject().Count() == allowed.Count && element.EnumerateObject().All(property => allowed.Contains(property.Name));
}

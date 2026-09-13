using System.Globalization;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Application.WorkItems;

public sealed class WorkItemEligibilityEvaluator : IWorkItemEligibilityEvaluator
{
    public ExclusionMatch? FindExclusion(RawWorkItem workItem, IReadOnlyList<ExclusionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            var field = workItem.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.ReferenceName, rule.Field, StringComparison.OrdinalIgnoreCase));

            // A missing or null field does not match an exclusion rule.
            if (field is null || field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (rule.Operator == ExclusionOperator.EqualsAny &&
                TryGetScalar(field.Value, out var actual) &&
                rule.Values.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase)))
            {
                return new ExclusionMatch(rule.Id, rule.Field);
            }
        }

        return null;
    }

    private static bool TryGetScalar(JsonElement value, out string result)
    {
        result = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };

        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }
}

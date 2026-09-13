using System.Text.RegularExpressions;

namespace IntakeGate.Application.Evidence;

public static partial class ValidatorCommentMarker
{
    public const string ValidatorIdentifier = "engineering-intake-gate";
    public const int ValidatorVersion = 1;

    public static string Create(Guid evaluationId)
    {
        if (evaluationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluationId), "Evaluation ID must be non-empty.");
        }

        return $"<!-- {ValidatorIdentifier}:validatorVersion={ValidatorVersion};evaluationId={evaluationId:D} -->";
    }

    public static bool IsPresent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var match = MarkerRegex().Match(content);
        return match.Success &&
               Guid.TryParseExact(match.Groups["evaluationId"].Value, "D", out var evaluationId) &&
               evaluationId != Guid.Empty;
    }

    [GeneratedRegex(
        @"<!--\s*engineering-intake-gate:validatorVersion=1;evaluationId=(?<evaluationId>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s*-->",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex MarkerRegex();
}

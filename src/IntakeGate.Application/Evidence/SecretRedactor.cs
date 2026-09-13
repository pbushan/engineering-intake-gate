using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace IntakeGate.Application.Evidence;

public sealed partial class SecretRedactor : ISecretRedactor
{
    public const string Placeholder = "[REDACTED_SECRET]";

    private static readonly RedactionRule[] Rules =
    [
        new("PrivateKey", PrivateKeyRegex()),
        new("AuthorizationHeader", AuthorizationHeaderRegex()),
        new("BearerToken", BearerTokenRegex()),
        new("CredentialAssignment", QuotedCredentialAssignmentRegex()),
        new("CredentialAssignment", CredentialAssignmentRegex()),
        new("RecognizableApiKey", RecognizableApiKeyRegex())
    ];

    public SecretRedactionResult Redact(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var redacted = content;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var rule in Rules)
        {
            var count = 0;
            redacted = rule.Pattern.Replace(redacted, match =>
            {
                count++;
                return match.Groups["prefix"].Success
                    ? match.Groups["prefix"].Value + Placeholder + match.Groups["suffix"].Value
                    : Placeholder;
            });

            if (count > 0)
            {
                counts[rule.Category] = counts.GetValueOrDefault(rule.Category) + count;
            }
        }

        return new SecretRedactionResult(
            redacted,
            counts.Values.Sum(),
            new ReadOnlyDictionary<string, int>(counts));
    }

    [GeneratedRegex(
        @"-----BEGIN (?<kind>(?:(?:RSA|EC|DSA|OPENSSH|ENCRYPTED) )?PRIVATE KEY|PGP PRIVATE KEY BLOCK)-----[\s\S]*?-----END \k<kind>-----",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(
        @"(?im)(?<prefix>\bauthorization\s*:\s*(?!bearer\b)[A-Za-z][A-Za-z0-9_-]*\s+)[^\s,;]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>\bbearer\s+)[A-Za-z0-9._~+\-/]+=*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        "(?im)(?<prefix>(?:\\\"|')?\\b(?:password|pwd|api[_-]?key|apikey|secret|token|accountkey|sharedaccesskey)(?:\\\"|')?\\s*[:=]\\s*(?:\\\"|'))[^\\\"'\\r\\n]*(?<suffix>(?:\\\"|'))",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex QuotedCredentialAssignmentRegex();

    [GeneratedRegex(
        "(?im)(?<prefix>(?:\\\"|')?\\b(?:password|pwd|api[_-]?key|apikey|secret|token|accountkey|sharedaccesskey)(?:\\\"|')?\\s*[:=]\\s*)(?!\\[REDACTED_SECRET\\])[^\\s\\\"'`,;&}\\]]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:sk-(?:test[_-])?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{16,}|AKIA[A-Z0-9]{16})(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex RecognizableApiKeyRegex();

    private sealed record RedactionRule(string Category, Regex Pattern);
}

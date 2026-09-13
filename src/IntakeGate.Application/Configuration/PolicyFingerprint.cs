using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntakeGate.Application.Configuration;

public static class PolicyFingerprint
{
    public static string Create(IntakePolicy policy)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", policy.Identity.Id);
            writer.WriteString("version", policy.Identity.Version);
            writer.WriteStartArray("criteria");
            foreach (var criterion in policy.Criteria)
            {
                writer.WriteStartObject();
                writer.WriteString("id", criterion.Id);
                writer.WriteString("displayName", criterion.DisplayName);
                writer.WriteString("description", criterion.Description);
                writer.WriteString("applicability", criterion.Applicability.ToString().ToLowerInvariant());
                writer.WriteStartObject("na");
                writer.WriteBoolean("allowed", criterion.NotApplicable.Allowed);
                writer.WriteBoolean("requiresExplanation", criterion.NotApplicable.RequiresExplanation);
                writer.WriteEndObject();
                writer.WriteString("evaluationGuidance", criterion.EvaluationGuidance);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(buffer.ToArray());
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

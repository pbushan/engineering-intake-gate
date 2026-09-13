using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Infrastructure.Ai;

internal static class ProviderJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    internal static object Schema => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "schemaVersion", "evaluationId", "decision", "applicableCriteria", "satisfiedCriteria", "deficiencies", "ambiguities", "engineeringSummary" },
        properties = new
        {
            schemaVersion = new { type = "string", @enum = new[] { "intake-evaluation-v1" } },
            evaluationId = new { type = "string" },
            decision = new { type = "string", @enum = new[] { "PASS", "FAIL" } },
            applicableCriteria = new { type = "array", items = new { type = "string" } },
            satisfiedCriteria = new { type = "array", items = new { type = "string" } },
            deficiencies = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "criterionId", "reason", "requiredSupportAction" },
                    properties = new { criterionId = new { type = "string" }, reason = new { type = "string" }, requiredSupportAction = new { type = "string" } }
                }
            },
            ambiguities = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "criterionId", "description", "requiredClarification" },
                    properties = new { criterionId = new { type = new[] { "string", "null" } }, description = new { type = "string" }, requiredClarification = new { type = "string" } }
                }
            },
            engineeringSummary = new { type = "string" }
        }
    };

    internal static string Input(EvaluationRequest request) => JsonSerializer.Serialize(new
    {
        request.EvaluationId,
        request.PromptVersion,
        request.PolicyId,
        request.PolicyVersion,
        request.PolicyFingerprint,
        request.Profile,
        request.Criteria,
        request.Evidence
    }, SerializerOptions);

    internal static object OpenAiInput(EvaluationRequest request)
    {
        var text = Input(request);
        if (request.VisualEvidence.Count == 0) return text;
        var content = new List<object> { new { type = "input_text", text } };
        content.AddRange(request.VisualEvidence.Select(visual => (object)new
        {
            type = "input_image",
            image_url = $"data:{visual.MediaType};base64,{Convert.ToBase64String(visual.Content.Span)}"
        }));
        return new[] { new { role = "user", content = content.ToArray() } };
    }

    internal static object AnthropicContent(EvaluationRequest request)
    {
        var text = Input(request);
        if (request.VisualEvidence.Count == 0) return text;
        var content = new List<object> { new { type = "text", text } };
        content.AddRange(request.VisualEvidence.Select(visual => (object)new
        {
            type = "image",
            source = new
            {
                type = "base64",
                media_type = visual.MediaType,
                data = Convert.ToBase64String(visual.Content.Span)
            }
        }));
        return content.ToArray();
    }

    internal static AiProviderFailure Failure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or (HttpStatusCode)429 =>
            new(AiProviderFailureKind.Transient, "ProviderThrottledOrUnavailable"),
        >= HttpStatusCode.InternalServerError =>
            new(AiProviderFailureKind.Transient, "ProviderServerFailure"),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new(AiProviderFailureKind.Permanent, "ProviderAuthenticationFailure"),
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity =>
            new(AiProviderFailureKind.Permanent, "ProviderInvalidRequest"),
        _ => new(AiProviderFailureKind.Permanent, "ProviderRequestFailure")
    };

    internal static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    internal static AiProviderAttemptMetadata Metadata(string body, string? headerRequestId, bool deriveTotal)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var requestId = String(root, "id") ?? String(root, "request_id") ?? headerRequestId;
            var model = String(root, "model");
            TokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageNode) && usageNode.ValueKind == JsonValueKind.Object &&
                TryInt(usageNode, "input_tokens", out var input) && TryInt(usageNode, "output_tokens", out var output))
            {
                var total = !deriveTotal && TryInt(usageNode, "total_tokens", out var suppliedTotal) ? suppliedTotal : input + output;
                usage = new TokenUsage(input, output, total);
            }
            return new(requestId, model, usage);
        }
        catch (JsonException)
        {
            return new(headerRequestId, null, null);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var node) && node.TryGetInt32(out value) && value >= 0;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

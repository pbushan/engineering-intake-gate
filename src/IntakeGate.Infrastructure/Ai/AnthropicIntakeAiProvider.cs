using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Evaluation;

namespace IntakeGate.Infrastructure.Ai;

public sealed class AnthropicIntakeAiProvider(
    HttpClient httpClient,
    IProviderCredentialResolver credentialResolver,
    string credentialEnvironmentVariable,
    TimeSpan timeout) : IIntakeAiProvider
{
    private static readonly Uri Endpoint = new(AiProviderHttp.AnthropicApiBase, "messages");

    public async Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            return AiProviderResponse.Failed(new(AiProviderFailureKind.Permanent, "ProviderInvalidConfiguration"));
        var credential = credentialResolver.Resolve(credentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
            return AiProviderResponse.Failed(new(AiProviderFailureKind.Permanent, "ProviderCredentialMissing"));

        using var message = AiProviderHttp.Anthropic(HttpMethod.Post, Endpoint,
            new IntakeGate.Application.Secrets.SecretValue(credential));
        message.Content = JsonContent.Create(new
        {
            model = request.ModelIdentifier,
            max_tokens = 4096,
            system = request.Prompt,
            messages = new[] { new { role = "user", content = ProviderJson.AnthropicContent(request) } },
            output_config = new { format = new { type = "json_schema", schema = ProviderJson.Schema } }
        }, options: ProviderJson.SerializerOptions);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiProviderResponse.Failed(new(AiProviderFailureKind.Transient, "ProviderTimeout"));
        }
        catch (HttpRequestException)
        {
            return AiProviderResponse.Failed(new(AiProviderFailureKind.Transient, "ProviderTransportFailure"));
        }

        using (response)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return AiProviderResponse.Failed(new(AiProviderFailureKind.Transient, "ProviderTimeout"));
            }
            var headerRequestId = ProviderJson.Header(response, "request-id");
            if (!response.IsSuccessStatusCode)
                return AiProviderResponse.Failed(ProviderJson.Failure(response.StatusCode), ProviderJson.Metadata(body, headerRequestId, deriveTotal: true));
            if (!TryMap(body, headerRequestId, out var payload, out var metadata))
                return AiProviderResponse.Success(body, new(headerRequestId, null, null));
            return AiProviderResponse.Success(payload!, metadata);
        }
    }

    private static bool TryMap(string body, string? headerRequestId, out string? payload, out AiProviderAttemptMetadata? metadata)
    {
        payload = null;
        metadata = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var requestId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : headerRequestId;
            var model = root.TryGetProperty("model", out var modelNode) && modelNode.ValueKind == JsonValueKind.String ? modelNode.GetString() : null;
            TokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageNode) && usageNode.ValueKind == JsonValueKind.Object &&
                TryInt(usageNode, "input_tokens", out var input) && TryInt(usageNode, "output_tokens", out var output))
                usage = new TokenUsage(input, output, input + output);
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                        item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        payload = text.GetString();
                        break;
                    }
                }
            }
            metadata = new(requestId, model, usage);
            return payload is not null;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var node) && node.TryGetInt32(out value) && value >= 0;
    }
}

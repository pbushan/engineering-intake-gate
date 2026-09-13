using System.Net.Http.Headers;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Infrastructure.Ai;

internal static class AiProviderHttp
{
    internal static readonly Uri OpenAiApiBase = new("https://api.openai.com/v1/");
    internal static readonly Uri AnthropicApiBase = new("https://api.anthropic.com/v1/");
    internal const string AnthropicVersion = "2023-06-01";

    internal static HttpRequestMessage OpenAi(HttpMethod method, Uri endpoint, SecretValue credential)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DangerousGetValue());
        return request;
    }

    internal static HttpRequestMessage Anthropic(HttpMethod method, Uri endpoint, SecretValue credential)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Add("x-api-key", credential.DangerousGetValue());
        request.Headers.Add("anthropic-version", AnthropicVersion);
        return request;
    }
}

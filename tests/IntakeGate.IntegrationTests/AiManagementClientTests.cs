using System.Net;
using System.Text;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Secrets;
using IntakeGate.Infrastructure.Ai;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AiManagementClientTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AI_MGMT_001_ProvidersUseSafeDocumentedModelDiscoveryContracts(string provider)
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = provider == AiProviderNames.OpenAi
                ? path.EndsWith("/models", StringComparison.Ordinal)
                    ? "{\"object\":\"list\",\"data\":[{\"id\":\"safe-model\",\"object\":\"model\",\"owned_by\":\"provider\"}]}"
                    : "{\"id\":\"safe-model\",\"object\":\"model\",\"owned_by\":\"provider\"}"
                : path.EndsWith("/models", StringComparison.Ordinal)
                    ? "{\"data\":[{\"id\":\"safe-model\",\"display_name\":\"Safe Model\",\"type\":\"model\"}],\"has_more\":false}"
                    : "{\"id\":\"safe-model\",\"display_name\":\"Safe Model\",\"type\":\"model\"}";
            return Json(HttpStatusCode.OK, body);
        });
        using var client = new HttpClient(handler);
        IAiManagementClient management = provider == AiProviderNames.OpenAi
            ? new OpenAiManagementClient(client, new SecretValue("synthetic-management-key"), TimeSpan.FromSeconds(5))
            : new AnthropicManagementClient(client, new SecretValue("synthetic-management-key"), TimeSpan.FromSeconds(5));

        Assert.True((await management.VerifyCredentialAsync()).Succeeded);
        var discovery = await management.DiscoverModelsAsync();
        Assert.True(discovery.Succeeded);
        var model = Assert.Single(discovery.Models);
        Assert.Equal(provider, model.Provider);
        Assert.Equal("safe-model", model.Id);
        var validation = await management.ValidateModelAsync("safe-model");
        Assert.True(validation.Succeeded);
        Assert.Equal("safe-model", validation.Model!.Id);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        if (provider == AiProviderNames.OpenAi)
            Assert.All(handler.Requests, request => Assert.Equal("Bearer synthetic-management-key", request.Authorization));
        else
        {
            Assert.All(handler.Requests, request => Assert.Equal("synthetic-management-key", request.ApiKey));
            Assert.All(handler.Requests, request => Assert.Equal("2023-06-01", request.Version));
        }
    }

    [Theory]
    [InlineData(401, AiManagementFailure.AuthenticationFailed)]
    [InlineData(403, AiManagementFailure.AuthorizationFailed)]
    [InlineData(404, AiManagementFailure.ModelNotFound)]
    [InlineData(429, AiManagementFailure.RateLimited)]
    [InlineData(503, AiManagementFailure.ProviderUnavailable)]
    public async Task AI_MGMT_002_ModelValidationClassifiesFailuresWithoutProviderBodies(
        int status, AiManagementFailure expected)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Json((HttpStatusCode)status,
            "{\"error\":\"provider-secret-body-canary\"}")));
        var management = new OpenAiManagementClient(client,
            new SecretValue("synthetic-management-key"), TimeSpan.FromSeconds(5));

        var result = await management.ValidateModelAsync("safe-model");

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Model);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(request.Method,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null));
            return Task.FromResult(response(request));
        }
    }

    private sealed record RequestRecord(HttpMethod Method, string? Authorization, string? ApiKey, string? Version);
}

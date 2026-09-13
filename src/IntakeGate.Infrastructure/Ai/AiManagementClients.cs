using System.Net;
using System.Text.Json;
using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Infrastructure.Ai;

public sealed class AiManagementClientFactory : IAiManagementClientFactory, IDisposable
{
    private readonly HttpClient httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public IAiManagementClient Create(string provider, SecretValue credential, TimeSpan timeout) => provider switch
    {
        AiProviderNames.OpenAi => new OpenAiManagementClient(httpClient, credential, timeout),
        AiProviderNames.Anthropic => new AnthropicManagementClient(httpClient, credential, timeout),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public void Dispose() => httpClient.Dispose();
}

/// <summary>
/// Deterministic management boundary for local black-box tests. The host only enables this
/// implementation through an explicit Development/Test setting; it never inspects the secret.
/// </summary>
public sealed class DeterministicAiManagementClientFactory : IAiManagementClientFactory
{
    public IAiManagementClient Create(string provider, SecretValue credential, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!AiProviderNames.TryNormalize(provider, out var normalized))
            throw new ArgumentOutOfRangeException(nameof(provider));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return new DeterministicAiManagementClient(normalized);
    }

    private sealed class DeterministicAiManagementClient(string provider) : IAiManagementClient
    {
        public Task<AiCredentialVerificationResult> VerifyCredentialAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiCredentialVerificationResult(true));
        }

        public Task<AiModelDiscoveryResult> DiscoverModelsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AiModelDescriptor> models = provider switch
            {
                AiProviderNames.OpenAi =>
                [
                    new(provider, "example-model", "Deterministic OpenAI example model"),
                    new(provider, "deterministic-openai-model", "Deterministic OpenAI model")
                ],
                AiProviderNames.Anthropic =>
                [
                    new(provider, "alternate-example-model", "Deterministic Anthropic example model"),
                    new(provider, "deterministic-anthropic-model", "Deterministic Anthropic model")
                ],
                _ => []
            };
            return Task.FromResult(new AiModelDiscoveryResult(true, models));
        }

        public Task<AiModelValidationResult> ValidateModelAsync(
            string modelId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiModelValidationResult(true,
                new AiModelDescriptor(provider, modelId, "Deterministic validated model")));
        }
    }
}

public sealed class OpenAiManagementClient(
    HttpClient httpClient,
    SecretValue credential,
    TimeSpan timeout,
    Uri? apiBase = null) : AiManagementClientBase(httpClient, credential, timeout,
        apiBase ?? AiProviderHttp.OpenAiApiBase, AiProviderNames.OpenAi)
{
    protected override HttpRequestMessage CreateGet(Uri endpoint) =>
        AiProviderHttp.OpenAi(HttpMethod.Get, endpoint, Credential);

    protected override IEnumerable<AiModelDescriptor> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Model list data is missing.");
        foreach (var item in data.EnumerateArray())
            if (ReadModel(item) is { } model) yield return model;
    }

    protected override AiModelDescriptor? ReadModel(JsonElement item)
    {
        if (!item.TryGetProperty("object", out var type) || type.GetString() != "model" ||
            !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        return new AiModelDescriptor(Provider, id.GetString() ?? string.Empty, null);
    }
}

public sealed class AnthropicManagementClient(
    HttpClient httpClient,
    SecretValue credential,
    TimeSpan timeout,
    Uri? apiBase = null) : AiManagementClientBase(httpClient, credential, timeout,
        apiBase ?? AiProviderHttp.AnthropicApiBase, AiProviderNames.Anthropic)
{
    protected override HttpRequestMessage CreateGet(Uri endpoint) =>
        AiProviderHttp.Anthropic(HttpMethod.Get, endpoint, Credential);

    protected override string ListPath(string? cursor) => string.IsNullOrEmpty(cursor)
        ? "models?limit=100"
        : $"models?limit=100&after_id={Uri.EscapeDataString(cursor)}";

    protected override string VerificationPath() => "models?limit=1";

    protected override string? NextCursor(JsonElement root)
    {
        var hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        return hasMore && root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String
            ? last.GetString()
            : null;
    }

    protected override IEnumerable<AiModelDescriptor> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Model list data is missing.");
        foreach (var item in data.EnumerateArray())
            if (ReadModel(item) is { } model) yield return model;
    }

    protected override AiModelDescriptor? ReadModel(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var type) || type.GetString() != "model" ||
            !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        var display = item.TryGetProperty("display_name", out var label) && label.ValueKind == JsonValueKind.String
            ? label.GetString()
            : null;
        return new AiModelDescriptor(Provider, id.GetString() ?? string.Empty, display);
    }
}

public abstract class AiManagementClientBase : IAiManagementClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumPages = 10;
    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;
    private readonly Uri apiBase;

    protected AiManagementClientBase(
        HttpClient httpClient, SecretValue credential, TimeSpan timeout, Uri apiBase, string provider)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        this.apiBase = apiBase ?? throw new ArgumentNullException(nameof(apiBase));
        Provider = provider;
    }

    protected SecretValue Credential { get; }
    protected string Provider { get; }

    public async Task<AiCredentialVerificationResult> VerifyCredentialAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await GetAsync(VerificationPath(), modelLookup: false, cancellationToken);
        using (result.Document)
            return result.Failure is null
                ? new AiCredentialVerificationResult(true)
                : new AiCredentialVerificationResult(false, result.Failure);
    }

    public async Task<AiModelDiscoveryResult> DiscoverModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var models = new List<AiModelDescriptor>();
        string? cursor = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var result = await GetAsync(ListPath(cursor), modelLookup: false, cancellationToken);
            if (result.Failure is { } failure) return new(false, [], failure);
            using var document = result.Document!;
            try
            {
                models.AddRange(ReadModels(document.RootElement));
                cursor = NextCursor(document.RootElement);
            }
            catch (JsonException)
            {
                return new(false, [], AiManagementFailure.InvalidProviderResponse);
            }
            if (string.IsNullOrEmpty(cursor)) return new(true, models);
        }
        return new(false, [], AiManagementFailure.InvalidProviderResponse);
    }

    public async Task<AiModelValidationResult> ValidateModelAsync(
        string modelId, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync($"models/{Uri.EscapeDataString(modelId)}", modelLookup: true, cancellationToken);
        if (result.Failure is { } failure) return new(false, null, failure);
        using var document = result.Document!;
        try
        {
            var model = ReadModel(document.RootElement);
            return model is null
                ? new(false, null, AiManagementFailure.InvalidProviderResponse)
                : new(true, model);
        }
        catch (JsonException)
        {
            return new(false, null, AiManagementFailure.InvalidProviderResponse);
        }
    }

    protected virtual string ListPath(string? cursor) => "models";
    protected virtual string VerificationPath() => ListPath(null);
    protected virtual string? NextCursor(JsonElement root) => null;
    protected abstract HttpRequestMessage CreateGet(Uri endpoint);
    protected abstract IEnumerable<AiModelDescriptor> ReadModels(JsonElement root);
    protected abstract AiModelDescriptor? ReadModel(JsonElement item);

    private async Task<GetResult> GetAsync(
        string path, bool modelLookup, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateGet(new Uri(apiBase, path));
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
                return GetResult.Failed(MapStatus(response.StatusCode, modelLookup));
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return GetResult.Failed(AiManagementFailure.InvalidProviderResponse);
            await using var input = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(bytes, timeoutSource.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    return GetResult.Failed(AiManagementFailure.InvalidProviderResponse);
                await buffer.WriteAsync(bytes.AsMemory(0, read), timeoutSource.Token);
            }
            buffer.Position = 0;
            return new(await JsonDocument.ParseAsync(buffer, cancellationToken: timeoutSource.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return GetResult.Failed(AiManagementFailure.Timeout); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return GetResult.Failed(AiManagementFailure.ProviderUnavailable);
        }
        catch (JsonException) { return GetResult.Failed(AiManagementFailure.InvalidProviderResponse); }
        catch { return GetResult.Failed(AiManagementFailure.UnexpectedFailure); }
    }

    private static AiManagementFailure MapStatus(HttpStatusCode status, bool modelLookup) => status switch
    {
        HttpStatusCode.Unauthorized => AiManagementFailure.AuthenticationFailed,
        HttpStatusCode.Forbidden => AiManagementFailure.AuthorizationFailed,
        HttpStatusCode.NotFound when modelLookup => AiManagementFailure.ModelNotFound,
        HttpStatusCode.TooManyRequests => AiManagementFailure.RateLimited,
        HttpStatusCode.RequestTimeout => AiManagementFailure.Timeout,
        >= HttpStatusCode.InternalServerError => AiManagementFailure.ProviderUnavailable,
        _ => AiManagementFailure.UnexpectedFailure
    };

    private sealed record GetResult(JsonDocument? Document, AiManagementFailure? Failure = null)
    {
        public static GetResult Failed(AiManagementFailure failure) => new(null, failure);
    }
}

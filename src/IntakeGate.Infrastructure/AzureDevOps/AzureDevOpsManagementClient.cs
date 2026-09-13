using System.Globalization;
using System.Net;
using System.Text.Json;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Infrastructure.AzureDevOps;

public sealed class AzureDevOpsManagementClientFactory : IAzureDevOpsManagementClientFactory
{
    private readonly HttpClient httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public IAzureDevOpsManagementClient Create(
        AzureDevOpsConfiguration configuration,
        SecretValue credential,
        int maximumRetries) =>
        new AzureDevOpsManagementClient(httpClient, configuration,
            credential, TimeSpan.FromSeconds(30), maximumRetries);
}

public sealed class AzureDevOpsManagementClient : IAzureDevOpsManagementClient
{
    private const int MaximumPages = 100;
    private readonly HttpClient httpClient;
    private readonly SecretValue credential;
    private readonly Uri projectApiBase;
    private readonly TimeSpan requestTimeout;
    private readonly int maximumRetries;

    public AzureDevOpsManagementClient(
        HttpClient httpClient,
        AzureDevOpsConfiguration configuration,
        SecretValue credential,
        TimeSpan requestTimeout,
        int maximumRetries = 0)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (maximumRetries < 0) throw new ArgumentOutOfRangeException(nameof(maximumRetries));
        this.requestTimeout = requestTimeout;
        this.maximumRetries = maximumRetries;
        projectApiBase = AzureDevOpsHttp.BuildProjectApiBase(configuration.OrganizationUrl, configuration.Project);
    }

    public async Task<AzureDevOpsConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("_apis/wit/workitemtypes?$top=1&api-version=7.1", false, cancellationToken);
        return response.Failure is null
            ? new AzureDevOpsConnectionResult(true)
            : new AzureDevOpsConnectionResult(false, response.Failure);
    }

    public async Task<AzureDevOpsQueryValidationResult> ValidateSavedQueryAsync(
        Guid queryId,
        CancellationToken cancellationToken = default)
    {
        if (queryId == Guid.Empty)
            return AzureDevOpsQueryValidationResult.Failed(queryId, AzureDevOpsManagementFailure.QueryNotFoundOrInaccessible);
        var ids = new List<int>();
        var seen = new HashSet<int>();
        string? continuation = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var path = $"_apis/wit/wiql/{queryId:D}?api-version=7.1";
            if (!string.IsNullOrEmpty(continuation))
                path += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            var response = await GetJsonAsync(path, true, cancellationToken);
            if (response.Failure is { } failure)
                return AzureDevOpsQueryValidationResult.Failed(queryId, failure);
            using var document = response.Document!;
            try
            {
                AddIds(document.RootElement, "workItems", ids, seen);
                if (document.RootElement.TryGetProperty("workItemRelations", out var relations) && relations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var relation in relations.EnumerateArray())
                    {
                        AddNestedId(relation, "source", ids, seen);
                        AddNestedId(relation, "target", ids, seen);
                    }
                }
                continuation = response.ContinuationToken ??
                    (document.RootElement.TryGetProperty("continuationToken", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return AzureDevOpsQueryValidationResult.Failed(queryId, AzureDevOpsManagementFailure.InvalidProviderResponse);
            }
            if (string.IsNullOrEmpty(continuation)) break;
            if (page == MaximumPages - 1)
                return AzureDevOpsQueryValidationResult.Failed(queryId, AzureDevOpsManagementFailure.InvalidProviderResponse);
        }

        var preview = new List<AzureDevOpsQueryPreviewItem>();
        foreach (var id in ids.Take(10))
        {
            var response = await GetJsonAsync(
                $"_apis/wit/workitems/{id}?fields=System.Title,System.WorkItemType,System.State&api-version=7.1",
                false, cancellationToken);
            if (response.Failure is { } failure)
                return AzureDevOpsQueryValidationResult.Failed(queryId, failure);
            using var document = response.Document!;
            try
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var actualId) || actualId != id ||
                    !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                    return AzureDevOpsQueryValidationResult.Failed(queryId, AzureDevOpsManagementFailure.InvalidProviderResponse);
                preview.Add(new AzureDevOpsQueryPreviewItem(id,
                    ReadString(fields, "System.Title"), ReadString(fields, "System.WorkItemType"),
                    ReadString(fields, "System.State"), new Uri(projectApiBase, $"_workitems/edit/{id}")));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return AzureDevOpsQueryValidationResult.Failed(queryId, AzureDevOpsManagementFailure.InvalidProviderResponse);
            }
        }
        return new AzureDevOpsQueryValidationResult(true, queryId, ids.Count, preview);
    }

    private async Task<JsonResult> GetJsonAsync(
        string path,
        bool queryOperation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= maximumRetries; attempt++)
        {
            var result = await GetJsonOnceAsync(path, queryOperation, cancellationToken);
            if (result.Failure is not (AzureDevOpsManagementFailure.Timeout or AzureDevOpsManagementFailure.ProviderUnavailable) ||
                attempt == maximumRetries) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
        }
        return JsonResult.Failed(AzureDevOpsManagementFailure.UnexpectedFailure);
    }

    private async Task<JsonResult> GetJsonOnceAsync(
        string path,
        bool queryOperation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = AzureDevOpsHttp.CreateGet(projectApiBase, path, credential.DangerousGetValue());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return JsonResult.Failed(MapStatus(response.StatusCode, queryOperation));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;
            return new JsonResult(document, continuation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return JsonResult.Failed(AzureDevOpsManagementFailure.Timeout);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return JsonResult.Failed(AzureDevOpsManagementFailure.ProviderUnavailable);
        }
        catch (JsonException)
        {
            return JsonResult.Failed(AzureDevOpsManagementFailure.InvalidProviderResponse);
        }
        catch
        {
            return JsonResult.Failed(AzureDevOpsManagementFailure.UnexpectedFailure);
        }
    }

    private static AzureDevOpsManagementFailure MapStatus(HttpStatusCode status, bool queryOperation) => status switch
    {
        HttpStatusCode.Unauthorized => AzureDevOpsManagementFailure.AuthenticationFailed,
        HttpStatusCode.Forbidden => AzureDevOpsManagementFailure.AuthorizationFailed,
        HttpStatusCode.NotFound when queryOperation => AzureDevOpsManagementFailure.QueryNotFoundOrInaccessible,
        HttpStatusCode.NotFound or HttpStatusCode.BadRequest => AzureDevOpsManagementFailure.OrganizationOrProjectUnavailable,
        HttpStatusCode.RequestTimeout => AzureDevOpsManagementFailure.Timeout,
        HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => AzureDevOpsManagementFailure.ProviderUnavailable,
        _ => AzureDevOpsManagementFailure.UnexpectedFailure
    };

    private static void AddIds(JsonElement root, string property, ICollection<int> ids, ISet<int> seen)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray()) AddId(value, ids, seen);
    }

    private static void AddNestedId(JsonElement root, string property, ICollection<int> ids, ISet<int> seen)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
            AddId(value, ids, seen);
    }

    private static void AddId(JsonElement value, ICollection<int> ids, ISet<int> seen)
    {
        if (value.TryGetProperty("id", out var id) && id.TryGetInt32(out var parsed) && parsed > 0 && seen.Add(parsed))
            ids.Add(parsed);
    }

    private static string ReadString(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed record JsonResult(
        JsonDocument? Document,
        string? ContinuationToken = null,
        AzureDevOpsManagementFailure? Failure = null)
    {
        public static JsonResult Failed(AzureDevOpsManagementFailure failure) => new(null, null, failure);
    }
}

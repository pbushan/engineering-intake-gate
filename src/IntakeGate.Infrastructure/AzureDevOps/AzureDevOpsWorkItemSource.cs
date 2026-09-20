using System.Globalization;
using System.Net;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Infrastructure.AzureDevOps;

public interface IAzureDevOpsCredentialResolver
{
    string? Resolve(string environmentVariableName);
}

public sealed class EnvironmentAzureDevOpsCredentialResolver : IAzureDevOpsCredentialResolver
{
    public string? Resolve(string environmentVariableName) =>
        string.IsNullOrWhiteSpace(environmentVariableName)
            ? null
            : Environment.GetEnvironmentVariable(environmentVariableName);
}

/// <summary>
/// Azure DevOps REST read adapter. Its public surface implements only the Application read port;
/// all provider transport and JSON details remain in Infrastructure.
/// </summary>
public sealed class AzureDevOpsWorkItemSource : IWorkItemSource
{
    private const int MaximumPages = 100;
    private readonly HttpClient httpClient;
    private readonly AzureDevOpsConfiguration configuration;
    private readonly IAzureDevOpsCredentialResolver credentialResolver;
    private readonly IWorkItemReadLog log;
    private readonly int maximumRetries;
    private readonly TimeSpan retryDelay;
    private readonly TimeSpan requestTimeout;
    private readonly long maximumAttachmentBytes;
    private readonly bool enableAttachmentDownloads;
    private readonly Uri projectApiBase;

    public AzureDevOpsWorkItemSource(
        HttpClient httpClient,
        AzureDevOpsConfiguration configuration,
        IAzureDevOpsCredentialResolver credentialResolver,
        IWorkItemReadLog log,
        int maximumRetries,
        long maximumAttachmentBytes,
        TimeSpan? retryDelay = null,
        TimeSpan? requestTimeout = null,
        bool enableAttachmentDownloads = true)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(log);
        if (maximumRetries < 0) throw new ArgumentOutOfRangeException(nameof(maximumRetries));
        if (maximumAttachmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttachmentBytes));

        this.httpClient = httpClient;
        this.configuration = configuration;
        this.credentialResolver = credentialResolver;
        this.log = log;
        this.maximumRetries = maximumRetries;
        this.maximumAttachmentBytes = maximumAttachmentBytes;
        this.enableAttachmentDownloads = enableAttachmentDownloads;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        projectApiBase = AzureDevOpsHttp.BuildProjectApiBase(configuration.OrganizationUrl, configuration.Project);
    }

    public async Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<int>();
        string? continuationToken = null;

        for (var page = 0; page < MaximumPages; page++)
        {
            var path = $"_apis/wit/wiql/{configuration.SavedQueryId:D}?api-version=7.1";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                path += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
            }

            var response = await SendJsonReadAsync("SavedQuery", null, path, cancellationToken);
            if (response.Failure is not null) return WorkItemQueryResult.Failed(response.Failure);

            try
            {
                using var document = response.Document!;
                if (document.RootElement.TryGetProperty("workItems", out var workItems) && workItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in workItems.EnumerateArray()) AddId(item, ids);
                }
                if (document.RootElement.TryGetProperty("workItemRelations", out var relations) && relations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var relation in relations.EnumerateArray())
                    {
                        if (relation.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object) AddId(source, ids);
                        if (relation.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object) AddId(target, ids);
                    }
                }
                continuationToken = response.ContinuationToken ?? ReadContinuationToken(document.RootElement);
                if (string.IsNullOrEmpty(continuationToken)) return new WorkItemQueryResult(ids.Order().ToArray());
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return WorkItemQueryResult.Failed(Permanent("AzureDevOpsInvalidQueryResponse"));
            }
        }

        return WorkItemQueryResult.Failed(Permanent("AzureDevOpsQueryPaginationLimitExceeded"));
    }

    public async Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        if (workItemId <= 0) return WorkItemReadResult.Failed(Permanent("InvalidWorkItemId"));

        var itemResponse = await SendJsonReadAsync(
            "WorkItem", workItemId,
            $"_apis/wit/workitems/{workItemId}?$expand=relations&api-version=7.1",
            cancellationToken);
        if (itemResponse.Failure is not null) return WorkItemReadResult.Failed(itemResponse.Failure);

        var commentsResult = await GetCommentsAsync(workItemId, cancellationToken);
        if (commentsResult.Failure is not null) return WorkItemReadResult.Failed(commentsResult.Failure);

        try
        {
            using var document = itemResponse.Document!;
            var root = document.RootElement;
            var actualId = root.GetProperty("id").GetInt32();
            if (actualId != workItemId)
                return WorkItemReadResult.Failed(Permanent("AzureDevOpsWorkItemIdentityMismatch"));
            var revision = root.GetProperty("rev").GetInt32().ToString(CultureInfo.InvariantCulture);
            var fieldsElement = root.TryGetProperty("fields", out var fieldsValue) && fieldsValue.ValueKind == JsonValueKind.Object
                ? fieldsValue
                : default;
            var fields = fieldsElement.ValueKind == JsonValueKind.Object
                ? fieldsElement.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new RawWorkItemField(
                        property.Name,
                        null,
                        property.Value.Clone(),
                        IsHtmlField(property.Name) ? WorkItemContentFormat.Html : WorkItemContentFormat.PlainText))
                    .ToArray()
                : [];

            var title = ReadStringField(fieldsElement, "System.Title");
            var workItemType = ReadStringField(fieldsElement, "System.WorkItemType");
            var description = ReadStringField(fieldsElement, "System.Description");
            DateTimeOffset? changedAtUtc = null;
            if (DateTimeOffset.TryParse(ReadStringField(fieldsElement, "System.ChangedDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsedChangedAt))
                changedAtUtc = parsedChangedAt.ToUniversalTime();
            var tags = ReadStringField(fieldsElement, "System.Tags")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var relations = new List<RawWorkItemRelation>();
            var attachments = new List<RawAttachmentMetadata>();
            if (root.TryGetProperty("relations", out var relationArray) && relationArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var relation in relationArray.EnumerateArray())
                {
                    var relationType = GetString(relation, "rel");
                    var reference = GetString(relation, "url");
                    var attributes = relation.TryGetProperty("attributes", out var attributeValue) && attributeValue.ValueKind == JsonValueKind.Object
                        ? attributeValue
                        : default;
                    var name = GetString(attributes, "name");
                    relations.Add(new RawWorkItemRelation(reference, relationType, EmptyToNull(name)));

                    if (!string.Equals(relationType, "AttachedFile", StringComparison.OrdinalIgnoreCase)) continue;
                    var attachmentId = AttachmentId(reference);
                    var size = GetInt64(attributes, "resourceSize");
                    attachments.Add(new RawAttachmentMetadata(
                        attachmentId,
                        string.IsNullOrWhiteSpace(name) ? attachmentId : name,
                        null,
                        size,
                        Content: enableAttachmentDownloads
                            ? new AzureDevOpsAttachmentContentSource(this, reference, size)
                            : null));
                }
            }

            return new WorkItemReadResult(new RawWorkItem(
                actualId.ToString(CultureInfo.InvariantCulture),
                revision,
                workItemType,
                title,
                fields,
                description,
                WorkItemContentFormat.Html,
                tags,
                relations,
                commentsResult.Comments,
                attachments,
                changedAtUtc));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return WorkItemReadResult.Failed(Permanent("AzureDevOpsInvalidWorkItemResponse"));
        }
    }

    private async Task<(IReadOnlyList<RawWorkItemComment> Comments, WorkItemReadFailure? Failure)> GetCommentsAsync(
        int workItemId,
        CancellationToken cancellationToken)
    {
        var comments = new List<RawWorkItemComment>();
        string? continuationToken = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var path = $"_apis/wit/workItems/{workItemId}/comments?$top=200&api-version=7.1-preview.4";
            if (!string.IsNullOrEmpty(continuationToken)) path += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
            var response = await SendJsonReadAsync("Comments", workItemId, path, cancellationToken);
            if (response.Failure is not null) return ([], response.Failure);

            using var document = response.Document!;
            try
            {
                if (!document.RootElement.TryGetProperty("comments", out var values) || values.ValueKind != JsonValueKind.Array)
                    return ([], Permanent("AzureDevOpsInvalidCommentsResponse"));
                foreach (var comment in values.EnumerateArray())
                {
                    var id = comment.TryGetProperty("id", out var idValue) ? idValue.ToString() : string.Empty;
                    var author = comment.TryGetProperty("createdBy", out var createdBy) ? GetString(createdBy, "displayName") : string.Empty;
                    DateTimeOffset? createdAt = null;
                    if (DateTimeOffset.TryParse(GetString(comment, "createdDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                        createdAt = parsed.ToUniversalTime();
                    comments.Add(new RawWorkItemComment(id, EmptyToNull(author), createdAt, GetString(comment, "text"), WorkItemContentFormat.Markdown));
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return ([], Permanent("AzureDevOpsInvalidCommentsResponse"));
            }

            continuationToken = response.ContinuationToken ?? ReadContinuationToken(document.RootElement);
            if (string.IsNullOrEmpty(continuationToken))
            {
                return (comments
                    .OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(comment => comment.Id, StringComparer.Ordinal)
                    .ToArray(), null);
            }
        }
        return ([], Permanent("AzureDevOpsCommentsPaginationLimitExceeded"));
    }

    private async Task<JsonReadResponse> SendJsonReadAsync(
        string operation,
        int? workItemId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumRetries + 1; attempt++)
        {
            var pat = credentialResolver.Resolve(configuration.Authentication.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(pat)) return JsonReadResponse.Failed(Permanent("AzureDevOpsCredentialUnavailable"));

            try
            {
                using var request = CreateGetRequest(relativePath, pat);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(requestTimeout);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = MapStatus(response.StatusCode);
                    if (failure.Kind == WorkItemReadFailureKind.Transient && attempt <= maximumRetries)
                    {
                        log.Retry(operation, workItemId, attempt, failure.SafeCategory);
                        await DelayAsync(attempt, cancellationToken);
                        continue;
                    }
                    return JsonReadResponse.Failed(failure);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                return new JsonReadResponse(document, ReadContinuationHeader(response));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                var failure = Transient(exception is OperationCanceledException ? "AzureDevOpsTimeout" : "AzureDevOpsTransportFailure");
                if (attempt <= maximumRetries)
                {
                    log.Retry(operation, workItemId, attempt, failure.SafeCategory);
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }
                return JsonReadResponse.Failed(failure);
            }
            catch (JsonException)
            {
                return JsonReadResponse.Failed(Permanent("AzureDevOpsInvalidJsonResponse"));
            }
        }
        throw new InvalidOperationException("Read retry bounds must produce a result.");
    }

    private async ValueTask<Stream> OpenAttachmentAsync(string providerUrl, long? declaredSize, CancellationToken cancellationToken)
    {
        if (declaredSize > maximumAttachmentBytes)
            throw new AttachmentContentUnavailableException("AttachmentDeclaredSizeLimitExceeded");
        if (!TryGetSafeAttachmentUri(providerUrl, out var attachmentUri))
            throw new AttachmentContentUnavailableException("AzureDevOpsUnsafeAttachmentReference");

        for (var attempt = 1; attempt <= maximumRetries + 1; attempt++)
        {
            var pat = credentialResolver.Resolve(configuration.Authentication.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(pat)) throw new AttachmentContentUnavailableException("AzureDevOpsCredentialUnavailable");
            try
            {
                using var request = CreateGetRequest(attachmentUri.AbsoluteUri, pat);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(requestTimeout);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = MapStatus(response.StatusCode);
                    response.Dispose();
                    if (failure.Kind == WorkItemReadFailureKind.Transient && attempt <= maximumRetries)
                    {
                        log.Retry("Attachment", null, attempt, failure.SafeCategory);
                        await DelayAsync(attempt, cancellationToken);
                        continue;
                    }
                    throw new AttachmentContentUnavailableException(failure.SafeCategory);
                }
                if (response.Content.Headers.ContentLength > maximumAttachmentBytes)
                {
                    response.Dispose();
                    throw new AttachmentContentUnavailableException("AttachmentResponseSizeLimitExceeded");
                }
                using (response)
                await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    return await BufferAttachmentAsync(stream, timeout.Token);
                }
            }
            catch (AttachmentContentUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                var category = exception is OperationCanceledException ? "AzureDevOpsTimeout" : "AzureDevOpsTransportFailure";
                if (attempt <= maximumRetries)
                {
                    log.Retry("Attachment", null, attempt, category);
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }
                throw new AttachmentContentUnavailableException(category);
            }
        }
        throw new AttachmentContentUnavailableException("AzureDevOpsAttachmentReadFailure");
    }

    private HttpRequestMessage CreateGetRequest(string relativePath, string pat)
        => AzureDevOpsHttp.CreateGet(projectApiBase, relativePath, pat);

    private bool TryGetSafeAttachmentUri(string providerUrl, out Uri attachmentUri)
    {
        attachmentUri = null!;
        if (providerUrl.Length > 2_048 || !Uri.TryCreate(providerUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (!string.Equals(uri.Scheme, projectApiBase.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, projectApiBase.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != projectApiBase.Port) return false;

        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (escapedPath.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            escapedPath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            escapedPath.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            escapedPath.Contains('\\')) return false;

        var organizationPath = configuration.OrganizationUrl.AbsolutePath.Trim('/');
        var projectPath = Uri.EscapeDataString(configuration.Project);
        var allowedPrefixes = new[]
        {
            $"{organizationPath}/_apis/wit/attachments/",
            $"{organizationPath}/{projectPath}/_apis/wit/attachments/"
        };
        var prefix = allowedPrefixes.FirstOrDefault(candidate =>
            escapedPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return false;
        var identifier = escapedPath[prefix.Length..];
        if (identifier.Contains('/') || !Guid.TryParseExact(identifier, "D", out _)) return false;

        var permitted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = component.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                if (key.Length > 32 || value.Length > 512) return false;
                if (!key.Equals("api-version", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("fileName", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                    !permitted.TryAdd(key, value) || value.Contains('\r') || value.Contains('\n')) return false;
            }
        }
        permitted["api-version"] = "7.1";
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        builder.Query = string.Join('&', permitted.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        attachmentUri = builder.Uri;
        return true;
    }

    private async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        if (retryDelay <= TimeSpan.Zero) return;
        await Task.Delay(TimeSpan.FromTicks(retryDelay.Ticks * attempt), cancellationToken);
    }

    private async Task<Stream> BufferAttachmentAsync(Stream source, CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Min(maximumAttachmentBytes, int.MaxValue);
        await using var buffer = new MemoryStream();
        var block = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(block.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > effectiveLimit)
                throw new AttachmentContentUnavailableException("AttachmentResponseSizeLimitExceeded");
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return new MemoryStream(buffer.ToArray(), writable: false);
    }

    private static void AddId(JsonElement value, ISet<int> ids)
    {
        if (value.TryGetProperty("id", out var id) && id.TryGetInt32(out var parsed) && parsed > 0) ids.Add(parsed);
    }

    private static string ReadContinuationToken(JsonElement root) =>
        root.TryGetProperty("continuationToken", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? ReadContinuationHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
            ? values.FirstOrDefault()
            : null;

    private static string ReadStringField(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsHtmlField(string name) =>
        name.Equals("System.Description", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Microsoft.VSTS.TCM.ReproSteps", StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long? GetInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string AttachmentId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } id ? id : url;
    }

    private static WorkItemReadFailure MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => Transient("AzureDevOpsRequestTimeout"),
        HttpStatusCode.TooManyRequests => Transient("AzureDevOpsThrottled"),
        >= HttpStatusCode.InternalServerError => Transient("AzureDevOpsServerFailure"),
        HttpStatusCode.Unauthorized => Permanent("AzureDevOpsAuthenticationFailure"),
        HttpStatusCode.Forbidden => Permanent("AzureDevOpsAuthorizationFailure"),
        HttpStatusCode.NotFound => Permanent("AzureDevOpsResourceNotFound"),
        HttpStatusCode.BadRequest => Permanent("AzureDevOpsInvalidRequest"),
        _ => Permanent("AzureDevOpsReadFailure")
    };

    private static WorkItemReadFailure Transient(string category) => new(WorkItemReadFailureKind.Transient, category);
    private static WorkItemReadFailure Permanent(string category) => new(WorkItemReadFailureKind.Permanent, category);

    private sealed record JsonReadResponse(JsonDocument? Document, string? ContinuationToken = null, WorkItemReadFailure? Failure = null)
    {
        public static JsonReadResponse Failed(WorkItemReadFailure failure) => new(null, null, failure);
    }

    private sealed class AzureDevOpsAttachmentContentSource(
        AzureDevOpsWorkItemSource owner,
        string providerUrl,
        long? length) : IAttachmentContentSource
    {
        public long? Length => length;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            owner.OpenAttachmentAsync(providerUrl, length, cancellationToken);
    }

}

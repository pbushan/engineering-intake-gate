using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;

namespace IntakeGate.Infrastructure.AzureDevOps;

/// <summary>
/// Deliberately small Azure DevOps mutation adapter.  It can replace the complete tag field
/// under a JSON Patch revision test or create an already-rendered validator comment; it cannot
/// patch arbitrary work-item fields, state, assignment, or identity.
/// </summary>
public sealed class AzureDevOpsWorkItemWriter : IWorkItemWriter
{
    private readonly HttpClient httpClient;
    private readonly AzureDevOpsConfiguration configuration;
    private readonly IAzureDevOpsCredentialResolver credentialResolver;
    private readonly TimeSpan requestTimeout;
    private readonly Uri projectApiBase;

    public AzureDevOpsWorkItemWriter(
        HttpClient httpClient,
        AzureDevOpsConfiguration configuration,
        IAzureDevOpsCredentialResolver credentialResolver,
        TimeSpan? requestTimeout = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        projectApiBase = BuildProjectApiBase(configuration.OrganizationUrl, configuration.Project);
    }

    public Task<WorkItemMutationResult> UpdateIntakeTagsAsync(
        IntakeTagUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkItemId <= 0 || !int.TryParse(request.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            return Task.FromResult(WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "InvalidMutationRequest"));
        if (request.FinalTags.Any(string.IsNullOrWhiteSpace))
            return Task.FromResult(WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "InvalidMutationRequest"));

        var finalTags = request.FinalTags.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var patch = JsonSerializer.Serialize(new object[]
        {
            new { op = "test", path = "/rev", value = revision },
            new { op = "add", path = "/fields/System.Tags", value = string.Join("; ", finalTags) }
        });
        return SendAsync(HttpMethod.Patch, $"_apis/wit/workitems/{request.WorkItemId}?api-version=7.1",
            patch, "application/json-patch+json", request.ExpectedRevision, cancellationToken, readRevision: true);
    }

    public Task<WorkItemMutationResult> AddValidatorCommentAsync(
        ValidatorCommentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkItemId <= 0 || string.IsNullOrWhiteSpace(request.Body) ||
            string.IsNullOrWhiteSpace(request.Marker) || !request.Body.Contains(request.Marker, StringComparison.Ordinal) ||
            !ValidatorCommentMarker.IsPresent(request.Marker))
            return Task.FromResult(WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "InvalidValidatorComment"));

        // The comment endpoint is not a JSON Patch resource. The application re-reads before
        // calling this endpoint; If-Match is included as an additional provider precondition
        // where the service honors it.
        return SendAsync(HttpMethod.Post,
            $"_apis/wit/workItems/{request.WorkItemId}/comments?format=markdown&api-version=7.1-preview.4",
            JsonSerializer.Serialize(new { text = request.Body }), "application/json", request.ExpectedRevision,
            cancellationToken, readRevision: false);
    }

    private async Task<WorkItemMutationResult> SendAsync(
        HttpMethod method,
        string path,
        string json,
        string mediaType,
        string expectedRevision,
        CancellationToken cancellationToken,
        bool readRevision)
    {
        var pat = credentialResolver.Resolve(configuration.Authentication.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pat))
            return WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsCredentialUnavailable");

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(projectApiBase, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedRevision}\"");
            request.Content = new StringContent(json, Encoding.UTF8, mediaType);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return Failure(response.StatusCode);

            if (!readRevision) return WorkItemMutationResult.Success(null, status);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return document.RootElement.TryGetProperty("rev", out var revision) && revision.TryGetInt32(out var parsed)
                ? WorkItemMutationResult.Success(parsed.ToString(CultureInfo.InvariantCulture), status)
                : WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsInvalidMutationResponse", status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Transient, "AzureDevOpsTimeout"); }
        catch (HttpRequestException) { return WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Transient, "AzureDevOpsTransportFailure"); }
        catch (JsonException) { return WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsInvalidMutationResponse"); }
    }

    private static WorkItemMutationResult Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Concurrency, "AzureDevOpsRevisionConflict", (int)status),
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Transient, "AzureDevOpsMutationUnavailable", (int)status),
        HttpStatusCode.Unauthorized => WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsAuthenticationFailure", (int)status),
        HttpStatusCode.Forbidden => WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsAuthorizationFailure", (int)status),
        _ => WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Permanent, "AzureDevOpsMutationRejected", (int)status)
    };

    private static Uri BuildProjectApiBase(Uri organizationUrl, string project) =>
        new(new Uri(organizationUrl.AbsoluteUri.TrimEnd('/') + "/"), Uri.EscapeDataString(project) + "/");
}

using System.Net;
using System.Text;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Secrets;
using IntakeGate.Infrastructure.AzureDevOps;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AzureDevOpsManagementClientTests
{
    private static readonly Guid QueryId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task ADO_MGMT_ConnectionAndEmptySavedQueryAreDistinctSuccessfulOperations()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/_apis/wit/workitemtypes", StringComparison.OrdinalIgnoreCase) =>
                Json(HttpStatusCode.OK, "{\"count\":1,\"value\":[]}"),
            var path when path.EndsWith($"/_apis/wit/wiql/{QueryId:D}", StringComparison.OrdinalIgnoreCase) =>
                Json(HttpStatusCode.OK, "{\"workItems\":[]}"),
            _ => Json(HttpStatusCode.NotFound, "{\"secretProviderBody\":true}")
        });
        var client = Client(handler);

        Assert.True((await client.TestConnectionAsync()).Succeeded);
        var validation = await client.ValidateSavedQueryAsync(QueryId);

        Assert.True(validation.Succeeded);
        Assert.Equal(0, validation.TotalCount);
        Assert.Empty(validation.Preview);
        Assert.All(handler.AuthorizationValues, value => Assert.StartsWith("Basic ", value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADO_MGMT_PreviewPreservesQueryOrderIsBoundedAndContainsOnlySafeFields()
    {
        var ids = Enumerable.Range(1, 12).Reverse().ToArray();
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/_apis/wit/wiql/{QueryId:D}", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK,
                    $"{{\"workItems\":[{string.Join(',', ids.Select(id => $"{{\"id\":{id}}}"))}]}}");
            var id = int.Parse(path.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
            return Json(HttpStatusCode.OK,
                $"{{\"id\":{id},\"fields\":{{\"System.Title\":\"Title {id}\",\"System.WorkItemType\":\"Bug\",\"System.State\":\"New\",\"System.Description\":\"must-not-escape\"}}}}");
        });

        var result = await Client(handler).ValidateSavedQueryAsync(QueryId);

        Assert.True(result.Succeeded);
        Assert.Equal(12, result.TotalCount);
        Assert.Equal(ids.Take(10), result.Preview.Select(item => item.Id));
        Assert.Equal(10, result.Preview.Count);
        Assert.DoesNotContain("must-not-escape", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AzureDevOpsManagementFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, AzureDevOpsManagementFailure.AuthorizationFailed)]
    [InlineData(HttpStatusCode.NotFound, AzureDevOpsManagementFailure.QueryNotFoundOrInaccessible)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AzureDevOpsManagementFailure.ProviderUnavailable)]
    public async Task ADO_MGMT_QueryFailuresReturnOnlySafeClassification(
        HttpStatusCode status,
        AzureDevOpsManagementFailure expected)
    {
        var result = await Client(new ScriptedHandler(_ => Json(status, "{\"pat\":\"must-not-escape\"}")))
            .ValidateSavedQueryAsync(QueryId);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure);
        Assert.DoesNotContain("must-not-escape", result.ToString(), StringComparison.Ordinal);
    }

    private static AzureDevOpsManagementClient Client(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/example"), "Engineering", QueryId,
            new CredentialReference("ADO_PAT")),
        new SecretValue("synthetic-management-pat"),
        TimeSpan.FromSeconds(2));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> AuthorizationValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationValues.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            return Task.FromResult(response(request));
        }
    }
}

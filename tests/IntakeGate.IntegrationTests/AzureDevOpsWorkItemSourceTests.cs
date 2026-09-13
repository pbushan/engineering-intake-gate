using System.Net;
using System.Text;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;
using IntakeGate.Application.Evaluation;
using IntakeGate.Infrastructure.AzureDevOps;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AzureDevOpsWorkItemSourceTests
{
    private const string QueryId = "11111111-1111-1111-1111-111111111111";
    private const string Pat = "SYNTHETIC_ADO_PAT_MUST_NOT_LEAK_801";

    [Fact]
    public async Task DISC_001_SavedQueryUsesConfiguredIdDeduplicatesAndPaginatesReadOnly()
    {
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, """{"workItems":[{"id":3},{"id":2},{"id":2}]}""", "next-page"),
            Json(HttpStatusCode.OK, """{"workItems":[{"id":1},{"id":3}]}"""));
        var source = Source(handler);

        var result = await source.ExecuteSavedQueryAsync();

        Assert.True(result.Succeeded);
        Assert.Equal([1, 2, 3], result.WorkItemIds);
        Assert.Contains($"/_apis/wit/wiql/{QueryId}", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.Contains("continuationToken=next-page", handler.Requests[1].Path, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("Basic", request.AuthorizationScheme));
        Assert.DoesNotContain(Pat, string.Join('|', handler.Requests.Select(request => request.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CNT_001_MapsFieldsRevisionCommentsRelationsAndAttachmentSourceGenerically()
    {
        var attachmentUrl = "https://dev.azure.com/generic-org/GenericProject/_apis/wit/attachments/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, $$$"""
                {
                  "id":42,
                  "rev":17,
                  "fields":{
                    "System.WorkItemType":"Generic Request",
                    "System.Title":"A title",
                    "System.Description":"<p>Description password=SYNTH_FIELD_SECRET_802</p>",
                    "System.Tags":"one; two",
                    "Custom.Structured":{"z":2,"a":true},
                    "Custom.Number":12.5
                  },
                  "relations":[
                    {"rel":"System.LinkTypes.Related","url":"https://example.invalid/work/9","attributes":{"name":"Related"}},
                    {"rel":"AttachedFile","url":"{{{attachmentUrl}}}","attributes":{"name":"notes.txt","resourceSize":46}}
                  ]
                }
                """),
            Json(HttpStatusCode.OK, """{"comments":[{"id":2,"text":"validator <!-- engineering-intake-gate:validatorVersion=1;evaluationId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee -->","createdDate":"2026-01-02T00:00:00Z","createdBy":{"displayName":"Bot"}}]}""", "comments-2"),
            Json(HttpStatusCode.OK, """{"comments":[{"id":1,"text":"Human context","createdDate":"2026-01-01T00:00:00Z","createdBy":{"displayName":"Person"}}]}"""),
            Bytes(HttpStatusCode.OK, "text/plain", "attachment password=SYNTH_ATTACHMENT_SECRET_803"),
            Bytes(HttpStatusCode.OK, "text/plain", "attachment password=SYNTH_ATTACHMENT_SECRET_803"));
        var source = Source(handler);

        var read = await source.GetWorkItemAsync(42);
        var item = Assert.IsType<RawWorkItem>(read.WorkItem);

        Assert.Equal("17", item.Revision);
        Assert.Equal("Generic Request", item.WorkItemType);
        Assert.Equal(["one", "two"], item.Tags);
        Assert.Contains(item.Fields, field => field.ReferenceName == "Custom.Structured" && field.Value.ValueKind == System.Text.Json.JsonValueKind.Object);
        Assert.Contains(item.Fields, field => field.ReferenceName == "Custom.Number" && field.Value.GetDecimal() == 12.5m);
        Assert.Equal(2, item.Comments.Count);
        Assert.Equal("Human context", item.Comments[0].Content);
        Assert.Equal(2, item.Relations.Count);
        var attachment = Assert.Single(item.Attachments);
        Assert.Equal("notes.txt", attachment.FileName);
        Assert.Equal(46, attachment.SizeBytes);

        await using var stream = await attachment.Content!.OpenReadAsync();
        using var reader = new StreamReader(stream);
        Assert.Contains("attachment password=", await reader.ReadToEndAsync(), StringComparison.Ordinal);

        var preprocessor = new EvidencePreprocessor(
            new IntakeGate.Infrastructure.Evidence.HtmlContentNormalizer(),
            new SecretRedactor(),
            new NullEvidenceLog(),
            new IntakeGate.Infrastructure.Evidence.AttachmentProcessingService([
                new IntakeGate.Infrastructure.Evidence.TextAttachmentProcessor()
            ]));
        var evidence = await preprocessor.PrepareAsync(item, Processing());
        var request = new EvaluationRequestBuilder().Build(evidence, Deployment());
        var serializedRequest = System.Text.Json.JsonSerializer.Serialize(request);
        Assert.Contains("Human context", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("validator <!--", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH_FIELD_SECRET_802", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH_ATTACHMENT_SECRET_803", serializedRequest, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_SECRET]", serializedRequest, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains(handler.Requests, request => request.Path.Contains("/comments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, request => request.Path.Contains("/_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task E4_AttachmentDownloadFailureIsUnavailableAndDoesNotCrashEvidencePipeline()
    {
        var attachmentUrl = "https://dev.azure.com/generic-org/GenericProject/_apis/wit/attachments/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, $$$"""{"id":42,"rev":8,"fields":{"System.WorkItemType":"Generic","System.Title":"Title"},"relations":[{"rel":"AttachedFile","url":"{{{attachmentUrl}}}","attributes":{"name":"failed.txt","resourceSize":20}}]}"""),
            Json(HttpStatusCode.OK, """{"comments":[]}"""),
            Json(HttpStatusCode.InternalServerError, "{}"));
        var source = Source(handler, retries: 0);
        var read = await source.GetWorkItemAsync(42);
        var preprocessor = new EvidencePreprocessor(
            new IntakeGate.Infrastructure.Evidence.HtmlContentNormalizer(),
            new SecretRedactor(),
            new NullEvidenceLog(),
            new IntakeGate.Infrastructure.Evidence.AttachmentProcessingService([
                new IntakeGate.Infrastructure.Evidence.TextAttachmentProcessor()
            ]));

        var evidence = await preprocessor.PrepareAsync(read.WorkItem!, Processing());

        var attachment = Assert.Single(evidence.Attachments);
        Assert.Equal(AttachmentProcessingStatus.Unavailable, attachment.ProcessingStatus);
        Assert.Equal("AzureDevOpsServerFailure", attachment.FailureCategory);
        Assert.False(evidence.Processing.AttachmentContentInspected);
    }

    [Fact]
    public async Task CNT_001_ImageAttachmentDownloadBecomesProviderNeutralVisualEvidence()
    {
        const string attachmentUrl = "https://dev.azure.com/generic-org/GenericProject/_apis/wit/attachments/image-1";
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var imageResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        imageResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, $$$"""{"id":88,"rev":13,"fields":{"System.WorkItemType":"Generic","System.Title":"Visual"},"relations":[{"rel":"AttachedFile","url":"{{{attachmentUrl}}}","attributes":{"name":"screen.png","resourceSize":{{{png.Length}}}}}]}"""),
            Json(HttpStatusCode.OK, """{"comments":[]}"""),
            imageResponse);
        var read = await Source(handler).GetWorkItemAsync(88);
        var preprocessor = new EvidencePreprocessor(
            new IntakeGate.Infrastructure.Evidence.HtmlContentNormalizer(),
            new SecretRedactor(), new NullEvidenceLog(),
            new IntakeGate.Infrastructure.Evidence.AttachmentProcessingService([
                new IntakeGate.Infrastructure.Evidence.ImageAttachmentProcessor()
            ]));

        var evidence = await preprocessor.PrepareAsync(read.WorkItem!, Processing());

        var attachment = Assert.Single(evidence.Attachments);
        Assert.Equal(AttachmentInspectionMode.Image, attachment.InspectionMode);
        Assert.Equal(AttachmentProcessingStatus.Processed, attachment.ProcessingStatus);
        var visual = Assert.Single(evidence.VisualEvidence);
        Assert.Equal("image/png", visual.MediaType);
        Assert.Equal(png, visual.Content.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WorkItemReadFailureKind.Permanent, "AzureDevOpsAuthenticationFailure")]
    [InlineData(HttpStatusCode.BadRequest, WorkItemReadFailureKind.Permanent, "AzureDevOpsInvalidRequest")]
    [InlineData(HttpStatusCode.ServiceUnavailable, WorkItemReadFailureKind.Transient, "AzureDevOpsServerFailure")]
    public async Task ADO_ReadFailuresAreSafeAndProviderNeutral(HttpStatusCode status, WorkItemReadFailureKind kind, string category)
    {
        var handler = new ScriptedHandler(Json(status, $"{{\"secret\":\"{Pat}\"}}"));
        var result = await Source(handler, retries: 0).ExecuteSavedQueryAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Equal(category, result.Failure.SafeCategory);
        Assert.DoesNotContain(Pat, result.Failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADO_TransientWorkItemReadRetriesWithoutDelayAndNeverMutates()
    {
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.ServiceUnavailable, "{}"),
            Json(HttpStatusCode.OK, """{"id":7,"rev":5,"fields":{"System.WorkItemType":"Generic","System.Title":"Recovered"},"relations":[]}"""),
            Json(HttpStatusCode.OK, """{"comments":[]}"""));

        var result = await Source(handler, retries: 1).GetWorkItemAsync(7);

        Assert.True(result.Succeeded);
        Assert.Equal("5", result.WorkItem!.Revision);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Method is not null &&
            request.Method != HttpMethod.Get);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Path.Contains("comments", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task SAFE_001_SAFE_002_NarrowWriterUsesRevisionTestAndOnlyTagCommentEndpoints()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, """{"id":42,"rev":8,"fields":{}}"""),
            Json(HttpStatusCode.OK, """{"id":3}"""));
        var writer = new AzureDevOpsWorkItemWriter(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/generic-org"), "GenericProject", Guid.Parse(QueryId), new CredentialReference("ADO_TEST_PAT")),
            new FixedCredentialResolver(Pat), TimeSpan.FromSeconds(5));

        var tags = await writer.UpdateIntakeTagsAsync(new IntakeTagUpdateRequest(42, "7", ["unrelated", "VALID"]));
        var comment = await writer.AddValidatorCommentAsync(new ValidatorCommentRequest(42, "8",
            "Validator result <!-- engineering-intake-gate:validatorVersion=1;evaluationId=11111111-2222-3333-4444-555555555555 -->",
            "<!-- engineering-intake-gate:validatorVersion=1;evaluationId=11111111-2222-3333-4444-555555555555 -->"));

        Assert.True(tags.Succeeded);
        Assert.Equal("8", tags.ResultingRevision);
        Assert.True(comment.Succeeded);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("/fields/System.Tags", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/rev\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Contains("/comments", handler.Requests[1].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engineering-intake-gate", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal("Basic", request.AuthorizationScheme));
        Assert.DoesNotContain(Pat, string.Join('|', handler.Requests.Select(request => request.Body)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SAFE_001_AC_20_E2_ProviderConcurrencyConflictIsClassifiedAndNeverRetriedBlindly()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.PreconditionFailed, "{}"));
        var writer = new AzureDevOpsWorkItemWriter(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/generic-org"), "GenericProject", Guid.Parse(QueryId), new CredentialReference("ADO_TEST_PAT")),
            new FixedCredentialResolver(Pat), TimeSpan.FromSeconds(5));

        var result = await writer.UpdateIntakeTagsAsync(new IntakeTagUpdateRequest(42, "7", ["VALID"]));

        Assert.False(result.Succeeded);
        Assert.Equal(WorkItemMutationFailureKind.Concurrency, result.FailureKind);
        Assert.Equal("AzureDevOpsRevisionConflict", result.SafeErrorCategory);
        Assert.Single(handler.Requests);
    }

    private static AzureDevOpsWorkItemSource Source(ScriptedHandler handler, int retries = 2) => new(
        new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
        new AzureDevOpsConfiguration(
            new Uri("https://dev.azure.com/generic-org"),
            "GenericProject",
            Guid.Parse(QueryId),
            new CredentialReference("ADO_TEST_PAT")),
        new FixedCredentialResolver(Pat),
        new NullWorkItemReadLog(),
        retries,
        1024 * 1024,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5));

    private static ProcessingConfiguration Processing() => new(
        ExecutionMode.DryRun, 1, 0,
        new ContentLimits(10_000, 20, 5_000),
        new AttachmentLimits(5, 1024 * 1024, 1024 * 1024, 10));

    private static DeploymentConfiguration Deployment() => new(
        new DeploymentProfile(
            new ProfileIdentity("generic", "1"),
            new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/generic-org"), "GenericProject", Guid.Parse(QueryId), new CredentialReference("ADO_TEST_PAT")),
            new AiConfiguration("fake", "model", new CredentialReference("AI_TEST")),
            new ScheduleConfiguration(false, "", "UTC", TimeSpan.Zero), Processing(), new AuditConfiguration(90), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("problem", "Problem", "Problem", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Guidance")]),
        "sha256:abc");

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string? continuation = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (continuation is not null) response.Headers.Add("x-ms-continuationtoken", continuation);
        return response;
    }

    private static HttpResponseMessage Bytes(HttpStatusCode status, string mediaType, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, mediaType)
    };

    private sealed class FixedCredentialResolver(string value) : IAzureDevOpsCredentialResolver
    {
        public string? Resolve(string environmentVariableName) => value;
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? AuthorizationScheme, string Body = "");

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Scheme,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty));
            if (responses.Count == 0) throw new InvalidOperationException("No scripted response remains.");
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class NullEvidenceLog : IEvidenceProcessingLog
    {
        public void ContentCollectionCompleted(ContentCollectionLogEntry entry) { }
        public void SecretRedactionCompleted(SecretRedactionLogEntry entry) { }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Time;
using IntakeGate.Infrastructure.Ai;
using IntakeGate.Infrastructure.Evidence;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AiProviderAdapterTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AI_001_AI_003_AC_18_ValidStructuredResponseAndMetadataMapThroughSharedContract(string provider)
    {
        var request = Request(provider);
        var handler = new StubHandler(_ => Response(provider, request, HttpStatusCode.OK));

        var result = await Adapter(provider, handler).EvaluateAsync(request, default);

        Assert.Null(result.Failure);
        Assert.Equal(ValidPayload(request), result.StructuredPayload);
        Assert.Equal(provider == "openai" ? "resp_test" : "msg_test", result.Metadata!.ProviderRequestId);
        Assert.Equal("reported-model", result.Metadata.ProviderReportedModel);
        Assert.Equal(11, result.Metadata.TokenUsage!.InputTokens);
        Assert.Equal(7, result.Metadata.TokenUsage.OutputTokens);
        Assert.Equal(18, result.Metadata.TokenUsage.TotalTokens);
        Assert.Contains(request.EvaluationId, handler.Body, StringComparison.Ordinal);
        Assert.Contains("json_schema", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task CNT_005_AC_09_ProviderNeutralVisualEvidenceMapsToNativeMultimodalRequest(string provider)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var request = Request(provider) with
        {
            VisualEvidence = [new VisualEvidence("image-1", "screenshot.png", "image/png", bytes)]
        };
        var handler = new StubHandler(_ => Response(provider, request, HttpStatusCode.OK));

        var result = await Adapter(provider, handler).EvaluateAsync(request, default);

        Assert.Null(result.Failure);
        using var document = JsonDocument.Parse(handler.Body);
        if (provider == "openai")
        {
            var content = document.RootElement.GetProperty("input")[0].GetProperty("content");
            Assert.Contains(content.EnumerateArray(), part => part.GetProperty("type").GetString() == "input_text");
            var image = content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_image");
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", image.GetProperty("image_url").GetString());
        }
        else
        {
            var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Contains(content.EnumerateArray(), part => part.GetProperty("type").GetString() == "text");
            var image = content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "image");
            Assert.Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal(Convert.ToBase64String(bytes), image.GetProperty("source").GetProperty("data").GetString());
        }
    }

    [Theory]
    [InlineData("openai", 429, AiProviderFailureKind.Transient, "ProviderThrottledOrUnavailable")]
    [InlineData("anthropic", 429, AiProviderFailureKind.Transient, "ProviderThrottledOrUnavailable")]
    [InlineData("openai", 503, AiProviderFailureKind.Transient, "ProviderServerFailure")]
    [InlineData("anthropic", 500, AiProviderFailureKind.Transient, "ProviderServerFailure")]
    [InlineData("openai", 401, AiProviderFailureKind.Permanent, "ProviderAuthenticationFailure")]
    [InlineData("anthropic", 403, AiProviderFailureKind.Permanent, "ProviderAuthenticationFailure")]
    [InlineData("openai", 400, AiProviderFailureKind.Permanent, "ProviderInvalidRequest")]
    [InlineData("anthropic", 404, AiProviderFailureKind.Permanent, "ProviderInvalidRequest")]
    public async Task AI_007_ProviderHttpFailuresMapToNeutralCategories(string provider, int status, AiProviderFailureKind kind, string category)
    {
        var result = await Adapter(provider, new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status))).EvaluateAsync(Request(provider), default);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Equal(category, result.Failure.SafeCategory);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AI_007_TransportAndTimeoutAreTransient(string provider)
    {
        var transport = await Adapter(provider, new StubHandler(_ => throw new HttpRequestException("synthetic"))).EvaluateAsync(Request(provider), default);
        var timeout = await Adapter(provider, new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), TimeSpan.FromMilliseconds(10)).EvaluateAsync(Request(provider), default);

        Assert.Equal("ProviderTransportFailure", transport.Failure!.SafeCategory);
        Assert.Equal(AiProviderFailureKind.Transient, transport.Failure.Kind);
        Assert.Equal("ProviderTimeout", timeout.Failure!.SafeCategory);
        Assert.Equal(AiProviderFailureKind.Transient, timeout.Failure.Kind);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AI_007_CallerCancellationPropagates(string provider)
    {
        var adapter = Adapter(provider, new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EvaluateAsync(Request(provider), source.Token));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task CFG_001_SEC_003_MissingCredentialFailsWithoutNetwork(string provider)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("network must not be called"));
        var adapter = Adapter(provider, handler, credential: null);
        var result = await adapter.EvaluateAsync(Request(provider), default);
        Assert.Equal(AiProviderFailureKind.Permanent, result.Failure!.Kind);
        Assert.Equal("ProviderCredentialMissing", result.Failure.SafeCategory);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AI_003_MalformedSuccessfulProviderPayloadIsNotRepaired(string provider)
    {
        var malformedWrapper = "not-json-from-provider";
        var result = await Adapter(provider, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(malformedWrapper)
        })).EvaluateAsync(Request(provider), default);
        Assert.Equal(malformedWrapper, result.StructuredPayload);
        Assert.False(new EvaluationResponseParser().TryParseResponse(result.StructuredPayload, out _, out _));
    }

    [Theory]
    [InlineData("openai", "{not-json")]
    [InlineData("anthropic", "{not-json")]
    [InlineData("openai", "{\"schemaVersion\":\"intake-evaluation-v1\"}")]
    [InlineData("anthropic", "{\"schemaVersion\":\"intake-evaluation-v1\"}")]
    [InlineData("openai", "```json\n{}\n```")]
    [InlineData("anthropic", "```json\n{}\n```")]
    public async Task AI_003_WrappedInvalidOutputReachesUnchangedPhase4Parser(string provider, string invalidPayload)
    {
        var result = await Adapter(provider, new StubHandler(_ => WrappedResponse(provider, invalidPayload)))
            .EvaluateAsync(Request(provider), default);
        Assert.Equal(invalidPayload, result.StructuredPayload);
        Assert.False(new EvaluationResponseParser().TryParseResponse(result.StructuredPayload, out _, out _));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task SEC_001_AC_15_RawSecretNeverReachesProviderBody(string provider)
    {
        const string secret = "SYNTHETIC_PROVIDER_SECRET_24680";
        var raw = new RawWorkItem("42", "1", "Generic", "Title", description: $"password={secret}");
        var processing = new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0,
            new ContentLimits(10_000, 10, 10_000), new AttachmentLimits(10, 1000, 5000, 10));
        var evidence = new EvidencePreprocessor(new HtmlContentNormalizer(), new SecretRedactor(), new NoOpEvidenceLog()).Prepare(raw, processing);
        var request = Request(provider) with { Evidence = evidence };
        var handler = new StubHandler(_ => Response(provider, request, HttpStatusCode.OK));

        await Adapter(provider, handler).EvaluateAsync(request, default);

        Assert.DoesNotContain(secret, handler.Body, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_SECRET]", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("RawWorkItem", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task SEC_003_CredentialIsHeaderOnlyAndNeverIncludedInBody(string provider)
    {
        const string credential = "synthetic-credential-never-log-or-body";
        var request = Request(provider);
        var handler = new StubHandler(_ => Response(provider, request, HttpStatusCode.OK));
        await Adapter(provider, handler, credential: credential).EvaluateAsync(request, default);
        Assert.DoesNotContain(credential, handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task AC_18_Phase4ValidationAndPhase5AuditArePortableAcrossConfiguredProviders(string provider)
    {
        var database = Path.Combine(Path.GetTempPath(), $"intake-gate-phase6-{Guid.NewGuid():N}.db");
        try
        {
            var handler = new StubHandler(async (httpRequest, token) =>
            {
                var body = await httpRequest.Content!.ReadAsStringAsync(token);
                using var outer = JsonDocument.Parse(body);
                var input = provider == "openai"
                    ? outer.RootElement.GetProperty("input").GetString()!
                    : outer.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                using var inner = JsonDocument.Parse(input);
                var request = Request(provider) with { EvaluationId = inner.RootElement.GetProperty("evaluationId").GetString()! };
                return Response(provider, request, HttpStatusCode.OK);
            });
            var configuration = Configuration(provider);
            await new SqliteDatabaseMigrator(database).MigrateAsync();
            var repository = new SqliteAuditRepository(database);
            var evaluationService = new IntakeEvaluationService(new EvaluationRequestBuilder(), Adapter(provider, handler),
                new EvaluationResponseParser(), new EvaluationContractValidator(), new NullEvaluationLog());
            var service = new IntakeRunService(
                new EvidencePreprocessor(new HtmlContentNormalizer(), new SecretRedactor(), new NoOpEvidenceLog()),
                evaluationService, new IntakeDecisionHandler(new IntakeCommentRenderer()), repository,
                new FixedClock(), new NullRunAuditLog(), new UnavailableAiCostAccountingService());

            var result = await service.ExecuteAsync(new RawWorkItem("42", "1", "Generic", "Title", description: "Useful context."),
                configuration, RunTriggerType.ManualWorkItem, "phase6-contract");
            var audit = await repository.GetEvaluationAsync(result.EvaluationId);

            Assert.Equal(EvaluationProcessingStatus.Completed, result.ProcessingStatus);
            Assert.Equal(provider, audit!.ProviderIdentifier);
            Assert.Equal(new TokenUsage(11, 7, 18), audit.TokenUsage);
            Assert.Equal("reported-model", audit.ProviderReportedModel);
            Assert.NotEmpty(audit.ProposedMutations);
            Assert.Empty(audit.AttemptedMutations);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public void CFG_001_SEC_003_EnvironmentCredentialResolutionUsesOnlyNamedExternalValue(string provider)
    {
        var name = $"INTAKE_GATE_{provider.ToUpperInvariant()}_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, "synthetic-external-value");
            Assert.Equal("synthetic-external-value", new EnvironmentProviderCredentialResolver().Resolve(name));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    private static IIntakeAiProvider Adapter(string provider, HttpMessageHandler handler, TimeSpan? timeout = null, string? credential = "synthetic-key")
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var resolver = new StubCredentialResolver(credential);
        return provider == "openai"
            ? new OpenAiIntakeAiProvider(client, resolver, "KEY_REF", timeout ?? TimeSpan.FromSeconds(5))
            : new AnthropicIntakeAiProvider(client, resolver, "KEY_REF", timeout ?? TimeSpan.FromSeconds(5));
    }

    private static EvaluationRequest Request(string provider) => new(
        "11111111-2222-3333-4444-555555555555", EvaluatorPrompt.Version, EvaluatorPrompt.Content,
        new EvaluationEvidence("42", "1", "Generic", "Title", [], "Description", [], [], [], [],
            new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0),
            new RedactionMetadata(false, 0, new Dictionary<string, int>())),
        "policy", "1", "sha256:test", new EvaluationProfileContext("profile", "1"),
        [new EvaluationCriterion("problem", "Problem", "A problem statement", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Assess clarity")],
        provider, "configured-model");

    private static DeploymentConfiguration Configuration(string provider) => new(
        new DeploymentProfile(new ProfileIdentity("profile", "1"),
            new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "project", Guid.NewGuid(), new CredentialReference("ADO")),
            new AiConfiguration(provider, "configured-model", new CredentialReference("KEY")) { TimeoutSeconds = 5 },
            new ScheduleConfiguration(false, string.Empty, "UTC", TimeSpan.FromDays(1)),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0, new ContentLimits(10_000, 10, 10_000), new AttachmentLimits(10, 1000, 5000, 10)),
            new AuditConfiguration(90), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("problem", "Problem", "A problem statement", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Assess clarity")]),
        "sha256:test");

    private static HttpResponseMessage Response(string provider, EvaluationRequest request, HttpStatusCode status)
    {
        object wrapper = provider == "openai"
            ? new { id = "resp_test", model = "reported-model", output_text = ValidPayload(request), usage = new { input_tokens = 11, output_tokens = 7, total_tokens = 18 } }
            : new { id = "msg_test", model = "reported-model", content = new[] { new { type = "text", text = ValidPayload(request) } }, usage = new { input_tokens = 11, output_tokens = 7 } };
        return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(wrapper), Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage WrappedResponse(string provider, string payload)
    {
        object wrapper = provider == "openai"
            ? new { id = "resp_invalid", model = "reported-model", output_text = payload, usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } }
            : new { id = "msg_invalid", model = "reported-model", content = new[] { new { type = "text", text = payload } }, usage = new { input_tokens = 1, output_tokens = 1 } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(wrapper), Encoding.UTF8, "application/json") };
    }

    private static string ValidPayload(EvaluationRequest request) => JsonSerializer.Serialize(new
    {
        schemaVersion = "intake-evaluation-v1",
        evaluationId = request.EvaluationId,
        decision = "PASS",
        applicableCriteria = new[] { "problem" },
        satisfiedCriteria = new[] { "problem" },
        deficiencies = Array.Empty<object>(),
        ambiguities = Array.Empty<object>(),
        engineeringSummary = "Grounded summary."
    });

    private sealed class StubCredentialResolver(string? value) : IProviderCredentialResolver
    {
        public string? Resolve(string environmentVariableName) => value;
    }

    private sealed class NoOpEvidenceLog : IEvidenceProcessingLog
    {
        public void ContentCollectionCompleted(ContentCollectionLogEntry entry) { }
        public void SecretRedactionCompleted(SecretRedactionLogEntry entry) { }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> action) : this((request, _) => Task.FromResult(action(request))) { }
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action;
        public string Body { get; private set; } = string.Empty;
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return await action(request, cancellationToken);
        }
    }
}

using System.Reflection;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class EvaluationContractTests
{
    [Fact]
    public void AI_001_AI_004_RequestIsDeterministicAndContainsOnlySanitizedEvaluationEvidence()
    {
        var evidence = Evidence();
        var configuration = Configuration();
        var request = new EvaluationRequestBuilder().Build(evidence, configuration, "11111111-2222-3333-4444-555555555555");

        Assert.Same(evidence, request.Evidence);
        Assert.Equal(["problem_statement", "reproduction_context"], request.Criteria.Select(item => item.Id));
        Assert.Equal(configuration.PolicyFingerprint, request.PolicyFingerprint);
        Assert.Equal(EvaluatorPrompt.Version, request.PromptVersion);
        Assert.Equal("generic-profile", request.Profile.ProfileId);
        Assert.DoesNotContain(typeof(RawWorkItem), typeof(EvaluationRequest).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void AI_005_PromptPreservesIntakeOnlyGroundedAndPartialEvidenceContract()
    {
        Assert.Contains("intake completeness only", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("defect versus enhancement", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("root cause", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("technical implementation", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ownership", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("severity or priority", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Material ambiguity defaults to `FAIL`", EvaluatorPrompt.Content, StringComparison.Ordinal);
        Assert.Contains("Ground every factual assertion", EvaluatorPrompt.Content, StringComparison.Ordinal);
        Assert.Contains("truncated, omitted, unsupported, unavailable, or not inspected", EvaluatorPrompt.Content, StringComparison.Ordinal);
        Assert.Contains("unknown scalar fields return null", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown list fields return an empty array", EvaluatorPrompt.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pass", IntakeDecision.Pass)]
    [InlineData("fail", IntakeDecision.Fail)]
    [InlineData("ambiguity", IntakeDecision.Fail)]
    [InlineData("explained-unknown", IntakeDecision.Pass)]
    [InlineData("unexplained-unknown", IntakeDecision.Fail)]
    [InlineData("alternative-evidence", IntakeDecision.Pass)]
    public async Task AI_002_AI_003_AC_11_AC_12_AC_13_ScriptedContractScenariosAreAccepted(string scenario, IntakeDecision expected)
    {
        var result = await Evaluate(FakeIntakeAiProvider.ForScenario(scenario));

        Assert.Equal(EvaluationProcessingStatus.Completed, result.ProcessingStatus);
        Assert.Equal(expected, result.Outcome);
        Assert.NotNull(result.Result);
        Assert.False(string.IsNullOrWhiteSpace(result.Result.TicketSummary.IssueSummary));
    }

    [Theory]
    [InlineData("PASS", true, false, "PassContainsGap")]
    [InlineData("PASS", false, true, "PassContainsGap")]
    [InlineData("FAIL", false, false, "FailWithoutGap")]
    public async Task E3_AI_007_ContradictoryOutcomesAreRejectedAndNeverTrusted(string decision, bool deficiency, bool ambiguity, string category)
    {
        var fake = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, decision, deficiency ? ["problem_statement"] : [], [], deficiency ? [Deficiency()] : [], ambiguity ? [Ambiguity()] : []), 3));

        var result = await Evaluate(fake);

        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Null(result.Outcome);
        Assert.Null(result.Result);
        Assert.Equal(category, result.Failure!.Category);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task AI_007_UnknownDuplicateAndOutsideApplicableCriteriaAreRejected()
    {
        var unknown = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "FAIL", ["unknown"], [], [Deficiency("unknown")], []), 3));
        var duplicate = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "FAIL", ["problem_statement", "problem_statement"], [], [Deficiency()], []), 3));
        var outsideApplicable = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "FAIL", [], [], [Deficiency()], []), 3));
        var satisfiedOutsideApplicable = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "PASS", [], ["problem_statement"], [], []), 3));
        var satisfiedAndDeficient = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "FAIL", ["problem_statement"], ["problem_statement"], [Deficiency()], []), 3));

        Assert.Equal("UnknownCriterionId", (await Evaluate(unknown)).Failure!.Category);
        Assert.Equal("DuplicateCriterionId", (await Evaluate(duplicate)).Failure!.Category);
        Assert.Equal("InconsistentCriterionClassification", (await Evaluate(outsideApplicable)).Failure!.Category);
        Assert.Equal("InconsistentCriterionClassification", (await Evaluate(satisfiedOutsideApplicable)).Failure!.Category);
        Assert.Equal("InconsistentCriterionClassification", (await Evaluate(satisfiedAndDeficient)).Failure!.Category);
    }

    [Fact]
    public async Task AI_007_E20_MalformedMissingUnknownAndForbiddenPropertiesAreRejected()
    {
        var malformed = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(_ => AiProviderResponse.Success("{bad"), 3));
        var missing = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => AiProviderResponse.Success(JsonSerializer.Serialize(new { schemaVersion = "intake-evaluation-v1", evaluationId = request.EvaluationId })), 3));
        var unknownDecision = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => Response(request, "MAYBE", [], [], [], []), 3));
        var unsupportedSchema = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => AiProviderResponse.Success(JsonSerializer.Serialize(new { schemaVersion = "intake-evaluation-v999", evaluationId = request.EvaluationId, decision = "PASS", applicableCriteria = Array.Empty<string>(), satisfiedCriteria = Array.Empty<string>(), deficiencies = Array.Empty<object>(), ambiguities = Array.Empty<object>(), engineeringSummary = "Summary.", ticketSummary = Summary() })), 3));
        var forbidden = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            request => AiProviderResponse.Success(JsonSerializer.Serialize(new { schemaVersion = "intake-evaluation-v2", evaluationId = request.EvaluationId, decision = "PASS", applicableCriteria = Array.Empty<string>(), satisfiedCriteria = Array.Empty<string>(), deficiencies = Array.Empty<object>(), ambiguities = Array.Empty<object>(), engineeringSummary = "Summary.", ticketSummary = Summary(), rootCause = "forbidden" })), 3));

        Assert.Equal("MalformedStructuredResponse", (await Evaluate(malformed)).Failure!.Category);
        Assert.Equal("MalformedStructuredResponse", (await Evaluate(missing)).Failure!.Category);
        Assert.Equal("UnknownDecision", (await Evaluate(unknownDecision)).Failure!.Category);
        Assert.Equal("UnsupportedSchemaVersion", (await Evaluate(unsupportedSchema)).Failure!.Category);
        Assert.Equal("MalformedStructuredResponse", (await Evaluate(forbidden)).Failure!.Category);
    }

    [Fact]
    public async Task AI_007_InvalidResponseRetriesThenAcceptsValidResponse()
    {
        var fake = new FakeIntakeAiProvider([
            _ => AiProviderResponse.Success("{bad"),
            FakeIntakeAiProvider.ValidPass]);

        var result = await Evaluate(fake);

        Assert.Equal(EvaluationProcessingStatus.Completed, result.ProcessingStatus);
        Assert.Equal(IntakeDecision.Pass, result.Outcome);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task AI_007_TransientAndTimeoutRetryButPermanentFailureDoesNot()
    {
        var transientThenPass = new FakeIntakeAiProvider([
            _ => AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Transient, "Transient")),
            FakeIntakeAiProvider.ValidPass]);
        var timeout = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            _ => AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Transient, "ProviderTimeout")), 3));
        var permanent = new FakeIntakeAiProvider([
            _ => AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Permanent, "Permanent"))]);

        Assert.Equal(IntakeDecision.Pass, (await Evaluate(transientThenPass)).Outcome);
        Assert.Equal(3, (await Evaluate(timeout)).Attempts);
        Assert.Equal(1, (await Evaluate(permanent)).Attempts);
    }

    [Fact]
    public async Task AI_007_ApplicationCancellationIsNotConvertedOrRetried()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var service = new IntakeEvaluationService(new EvaluationRequestBuilder(), FakeIntakeAiProvider.ForScenario("pass"),
            new EvaluationResponseParser(), new EvaluationContractValidator(), new NullEvaluationLog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(Evidence(), Configuration(), source.Token));
    }

    [Fact]
    public void AI_006_ProviderAndTrustedResultExposeNoMutationAuthority()
    {
        var boundaryTypes = new[] { typeof(IIntakeAiProvider), typeof(AiProviderResponse), typeof(EvaluationResult), typeof(EvaluationRequest) };
        var banned = new[] { "ado", "tag", "comment", "assign", "state", "mutation", "update", "workitem" };

        foreach (var type in boundaryTypes)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(member => member.Name).ToArray();
            Assert.DoesNotContain(members, member => banned.Any(word => member.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }

        var providerParameters = typeof(IIntakeAiProvider).GetMethods().SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType);
        var serviceParameters = typeof(IIntakeEvaluationService).GetMethods().SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType);
        Assert.DoesNotContain(typeof(RawWorkItem), providerParameters);
        Assert.DoesNotContain(typeof(RawWorkItem), serviceParameters);
    }

    private static async Task<EvaluationProcessingResult> Evaluate(IIntakeAiProvider provider) =>
        await new IntakeEvaluationService(new EvaluationRequestBuilder(), provider, new EvaluationResponseParser(), new EvaluationContractValidator(), new NullEvaluationLog())
            .EvaluateAsync(Evidence(), Configuration());

    private static AiProviderResponse Response(EvaluationRequest request, string decision, IEnumerable<string> applicable, IEnumerable<string> satisfied, object[] deficiencies, object[] ambiguities) =>
        FakeIntakeAiProvider.Json(request, decision, applicable, satisfied, deficiencies, ambiguities, "Known evidence summary.");

    private static object Deficiency(string criterion = "problem_statement") => new { criterionId = criterion, reason = "Evidence is insufficient.", requiredSupportAction = "Provide the missing context." };
    private static object Ambiguity() => new { criterionId = (string?)null, description = "Evidence is unclear.", requiredClarification = "Clarify the missing context." };
    private static object Summary() => new
    {
        issueSummary = "Known evidence summary.",
        expectedBehavior = (string?)null,
        actualBehavior = (string?)null,
        reproductionSteps = Array.Empty<string>(),
        affectedExamples = Array.Empty<string>(),
        environment = (string?)null,
        businessImpact = (string?)null,
        attachmentFindings = Array.Empty<string>(),
        investigationWarnings = Array.Empty<string>()
    };

    private static DeploymentConfiguration Configuration(int retries = 2) => new(
        new DeploymentProfile(
            new ProfileIdentity("generic-profile", "1"), new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("INTAKE-VALIDATED", "INTAKE-INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "project", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), new CredentialReference("ADO_REFERENCE")),
            new AiConfiguration("fake", "scripted", new CredentialReference("AI_REFERENCE")),
            new ScheduleConfiguration(false, "0 0 * * *", "UTC", TimeSpan.Zero),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, retries, new ContentLimits(1_000, 10, 500), new AttachmentLimits(0, 0, 0, 0)),
            new AuditConfiguration(1), []),
        new IntakePolicy(new PolicyIdentity("generic-policy", "1"),
        [
            new IntakeCriterion("problem_statement", "Problem", "Observed behavior.", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Assess useful context."),
            new IntakeCriterion("reproduction_context", "Reproduction", "Reproduction context.", CriterionApplicability.Contextual, new NotApplicablePolicy(true, true), "Assess contextual relevance.")
        ]),
        "sha256:0123456789abcdef");

    private static EvaluationEvidence Evidence() => new(
        "item-1", "1", "Generic", "Safe title", [], "Safe sanitized description.", [], [], [], [],
        new EvidenceProcessingDisclosure(
            TruncationOccurred: false, AggregateTextLimitReached: false, WorkItemTypeTruncated: false, TitleTruncated: false,
            DescriptionTruncated: false, TruncatedFieldCount: 0, OmittedFieldCount: 0, IncludedTagCount: 0,
            AvailableTagCount: 0, TruncatedTagCount: 0, OmittedTagCount: 0, IncludedRelationCount: 0,
            AvailableRelationCount: 0, TruncatedRelationCount: 0, OmittedRelationCount: 0, IncludedCommentCount: 0,
            AvailableHumanCommentCount: 0, ExcludedValidatorCommentCount: 0, TruncatedCommentCount: 0,
            OmittedCommentCount: 0, IncludedAttachmentMetadataCount: 0, AvailableAttachmentMetadataCount: 0,
            TruncatedAttachmentMetadataCount: 0, OmittedAttachmentMetadataCount: 0, AttachmentMetadataAvailable: false,
            AttachmentContentInspected: false, IncludedTextCharacters: 25),
        new RedactionMetadata(false, 0, new Dictionary<string, int>()));
}

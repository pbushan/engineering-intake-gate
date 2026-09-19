using System.Text.Json;

namespace IntakeGate.Application.Evaluation;

/// <summary>A deterministic scripted double. It intentionally performs no evidence evaluation.</summary>
public sealed class FakeIntakeAiProvider : IIntakeAiProvider
{
    private readonly Queue<Func<EvaluationRequest, AiProviderResponse>> steps;
    private readonly Func<EvaluationRequest, AiProviderResponse>? repeat;

    public FakeIntakeAiProvider(IEnumerable<Func<EvaluationRequest, AiProviderResponse>> script)
        : this(script, null) { }

    private FakeIntakeAiProvider(
        IEnumerable<Func<EvaluationRequest, AiProviderResponse>> script,
        Func<EvaluationRequest, AiProviderResponse>? repeat)
    {
        steps = new Queue<Func<EvaluationRequest, AiProviderResponse>>(script);
        this.repeat = repeat;
    }

    public Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (steps.Count == 0)
        {
            if (repeat is not null) return Task.FromResult(repeat(request));
            return Task.FromResult(AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Permanent, "FakeScriptExhausted")));
        }
        return Task.FromResult(steps.Dequeue()(request));
    }

    public static FakeIntakeAiProvider ReusablePass() => new([], request => ValidPass(request));

    public static FakeIntakeAiProvider ForScenario(string scenario) => scenario.ToLowerInvariant() switch
    {
        "pass" or "complete" or "explained-unknown" or "alternative-evidence" => new FakeIntakeAiProvider([request => ValidPass(request)]),
        "fail" or "missing-evidence" or "unexplained-unknown" => new FakeIntakeAiProvider([request => ValidFail(request)]),
        "ambiguity" => new FakeIntakeAiProvider([request => ValidAmbiguityFail(request)]),
        "e3" or "contradictory-pass" => new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(request => ContradictoryPass(request), 3)),
        "e20" or "malformed" => new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(_ => AiProviderResponse.Success("{not-json"), 3)),
        _ => new FakeIntakeAiProvider([_ => AiProviderResponse.Failed(new AiProviderFailure(AiProviderFailureKind.Permanent, "UnknownFakeScenario"))])
    };

    public static AiProviderResponse ValidPass(EvaluationRequest request) => Json(request, "PASS", request.Criteria.Select(item => item.Id), request.Criteria.Select(item => item.Id), [], [], "The supplied evidence contains useful investigation context.");
    public static AiProviderResponse ValidFail(EvaluationRequest request)
    {
        var criterion = request.Criteria[0].Id;
        return Json(request, "FAIL", [criterion], [], [new { criterionId = criterion, reason = "The supplied evidence does not establish this context.", requiredSupportAction = "Provide the missing investigation context." }], [], "Known context is retained, but a material gap remains.");
    }
    public static AiProviderResponse ValidAmbiguityFail(EvaluationRequest request) =>
        Json(request, "FAIL", [], [], [], [new { criterionId = (string?)null, description = "The reported behavior is unclear.", requiredClarification = "Clarify the observed behavior and expected outcome." }], "The current evidence is ambiguous.");
    public static AiProviderResponse ContradictoryPass(EvaluationRequest request)
    {
        var criterion = request.Criteria[0].Id;
        return Json(request, "PASS", [criterion], [], [new { criterionId = criterion, reason = "Missing context.", requiredSupportAction = "Provide it." }], [], "Unsafe response.");
    }

    public static AiProviderResponse Json(
        EvaluationRequest request,
        string decision,
        IEnumerable<string> applicableCriteria,
        IEnumerable<string> satisfiedCriteria,
        object[] deficiencies,
        object[] ambiguities,
        string engineeringSummary) => AiProviderResponse.Success(JsonSerializer.Serialize(new
        {
            schemaVersion = "intake-evaluation-v2",
            evaluationId = request.EvaluationId,
            decision,
            applicableCriteria,
            satisfiedCriteria,
            deficiencies,
            ambiguities,
            engineeringSummary,
            ticketSummary = new
            {
                issueSummary = engineeringSummary,
                expectedBehavior = (string?)null,
                actualBehavior = (string?)null,
                reproductionSteps = Array.Empty<string>(),
                affectedExamples = Array.Empty<string>(),
                environment = (string?)null,
                businessImpact = (string?)null,
                attachmentFindings = Array.Empty<string>(),
                investigationWarnings = Array.Empty<string>()
            }
        }));
}

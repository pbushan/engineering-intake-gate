using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Audit;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class DecisionHandlerTests
{
    private static readonly Uri PolicyUrl = new("https://example.invalid/intake-policy");
    private static readonly IntakeGate.Application.Configuration.IntakeStateConfiguration Tags =
        new("INTAKE-VALIDATED", "INTAKE-INCOMPLETE");

    [Theory]
    [InlineData(IntakeDecision.Pass, "", "AddTag:INTAKE-VALIDATED,PostComment:")]
    [InlineData(IntakeDecision.Pass, "INTAKE-INCOMPLETE", "RemoveTag:INTAKE-INCOMPLETE,AddTag:INTAKE-VALIDATED,PostComment:")]
    [InlineData(IntakeDecision.Pass, "INTAKE-VALIDATED", "PostComment:")]
    [InlineData(IntakeDecision.Fail, "", "AddTag:INTAKE-INCOMPLETE,PostComment:")]
    [InlineData(IntakeDecision.Fail, "INTAKE-VALIDATED", "RemoveTag:INTAKE-VALIDATED,AddTag:INTAKE-INCOMPLETE,PostComment:")]
    [InlineData(IntakeDecision.Fail, "INTAKE-INCOMPLETE", "PostComment:")]
    public void DRY_003_AC_01_AC_02_DecisionHandlerOwnsIdempotentOrderedMappings(
        IntakeDecision intakeDecision,
        string currentTag,
        string expectedOrder)
    {
        var handler = new IntakeDecisionHandler(new IntakeCommentRenderer());
        var result = handler.Decide(Completed(intakeDecision),
            string.IsNullOrEmpty(currentTag) ? [] : [currentTag], Tags, PolicyUrl);

        Assert.Equal(expectedOrder, string.Join(',', result.ProposedMutations.Select(item => $"{item.Type}:{item.Tag}")));
        Assert.Equal(ProposedMutationType.PostComment, result.ProposedMutations[^1].Type);
    }

    [Fact]
    public void SAFE_001_ErrorProducesNoProposedEnforcementActions()
    {
        var error = new EvaluationProcessingResult(
            EvaluationProcessingStatus.Error, null, new EvaluationFailure("SyntheticFailure"), 3);

        var result = new IntakeDecisionHandler(new IntakeCommentRenderer()).Decide(
            error, [Tags.ValidatedTag, Tags.IncompleteTag], Tags, PolicyUrl);

        Assert.Empty(result.ProposedMutations);
        Assert.Null(result.RenderedCommentBody);
    }

    [Fact]
    public void AUD_001_PassCommentIsApplicationOwnedGroundedAndMarked()
    {
        var evaluation = Completed(IntakeDecision.Pass);
        var result = new IntakeDecisionHandler(new IntakeCommentRenderer()).Decide(evaluation, [], Tags, PolicyUrl);
        var comment = Assert.Single(result.ProposedMutations, item => item.Type == ProposedMutationType.PostComment);

        Assert.Contains("✅ Engineering intake validated", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Trusted engineering summary.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("sufficient information for Engineering to begin investigation", comment.Body, StringComparison.Ordinal);
        Assert.Contains("does not confirm defect classification, root cause, severity, priority, solution, or ownership", comment.Body, StringComparison.Ordinal);
        Assert.Contains("This ticket was reviewed using AI and this comment was AI-generated.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("evaluationId=11111111-2222-3333-4444-555555555555", comment.Body, StringComparison.Ordinal);
        Assert.Equal("<!-- engineering-intake-gate:validatorVersion=1;evaluationId=11111111-2222-3333-4444-555555555555 -->", comment.CommentMarker);
    }

    [Fact]
    public void AUD_001_FailCommentContainsOnlyActionableCurrentGapsPolicyAndMarker()
    {
        var evaluation = Completed(IntakeDecision.Fail);
        var result = new IntakeDecisionHandler(new IntakeCommentRenderer()).Decide(evaluation, [], Tags, PolicyUrl);
        var comment = Assert.Single(result.ProposedMutations, item => item.Type == ProposedMutationType.PostComment);

        Assert.Contains("Engineering intake incomplete", comment.Body, StringComparison.Ordinal);
        Assert.Contains("problem_statement: Trusted missing reason.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Support action: Supply trusted evidence.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Trusted ambiguity.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Support action: Clarify trusted ambiguity.", comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("satisfied_only", comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Trusted engineering summary.", comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", comment.Body, StringComparison.Ordinal);
        Assert.Contains(PolicyUrl.AbsoluteUri, comment.Body, StringComparison.Ordinal);
        Assert.Contains("intake completeness only", comment.Body, StringComparison.Ordinal);
        Assert.Contains("does not classify the ticket as a defect or enhancement", comment.Body, StringComparison.Ordinal);
        Assert.Contains("This ticket was reviewed using AI and this comment was AI-generated.", comment.Body, StringComparison.Ordinal);
        Assert.Contains("evaluationId=11111111-2222-3333-4444-555555555555", comment.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AI_006_ProviderAndEvaluationResultCannotCarryProposedMutations()
    {
        var prohibited = typeof(ProposedMutation);
        var contractTypes = new[] { typeof(EvaluationRequest), typeof(AiProviderResponse), typeof(EvaluationResult) };

        Assert.All(contractTypes, type => Assert.DoesNotContain(type.GetProperties(), property =>
            property.PropertyType == prohibited ||
            (property.PropertyType.IsGenericType && property.PropertyType.GenericTypeArguments.Contains(prohibited))));
        Assert.All(typeof(IIntakeAiProvider).GetMethods(), method => Assert.NotEqual(prohibited, method.ReturnType));
    }

    [Fact]
    public void AUD_003_AllRequiredTriggerTypesAreModeled()
    {
        Assert.Equal(
            [RunTriggerType.Scheduled, RunTriggerType.ManualIncremental, RunTriggerType.ManualWorkItem],
            Enum.GetValues<RunTriggerType>());
    }

    [Fact]
    public void DRY_003_IdenticalInputsProduceIdenticallyOrderedProposals()
    {
        var handler = new IntakeDecisionHandler(new IntakeCommentRenderer());
        var evaluation = Completed(IntakeDecision.Pass);

        var first = handler.Decide(evaluation, [Tags.IncompleteTag], Tags, PolicyUrl);
        var second = handler.Decide(evaluation, [Tags.IncompleteTag], Tags, PolicyUrl);

        Assert.Equal(first.ProposedMutations, second.ProposedMutations);
    }

    [Fact]
    public void AC_11_AmbiguityOnlyFailRendersAnActionableClarificationWithoutInventingDeficiency()
    {
        var result = new EvaluationResult(
            "11111111-2222-3333-4444-555555555555", IntakeDecision.Fail, "policy", "1", "sha256:abc", "prompt-v1",
            [], [], [], [new EvaluationAmbiguity(null, "Trusted unclear behavior.", "State the observed and expected behavior.")],
            "Trusted summary.", "fake", "scripted");

        var decision = new IntakeDecisionHandler(new IntakeCommentRenderer()).Decide(
            new EvaluationProcessingResult(EvaluationProcessingStatus.Completed, result, null, 1), [], Tags, PolicyUrl);
        var comment = decision.ProposedMutations.Single(item => item.Type == ProposedMutationType.PostComment).Body!;

        Assert.Contains("Trusted unclear behavior.", comment, StringComparison.Ordinal);
        Assert.Contains("State the observed and expected behavior.", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Missing or insufficient information", comment, StringComparison.Ordinal);
    }

    private static EvaluationProcessingResult Completed(IntakeDecision decision)
    {
        var deficiencies = decision == IntakeDecision.Fail
            ? new[] { new EvaluationDeficiency("problem_statement", "Trusted missing reason.", "Supply trusted evidence.") }
            : [];
        var ambiguities = decision == IntakeDecision.Fail
            ? new[] { new EvaluationAmbiguity(null, "Trusted ambiguity.", "Clarify trusted ambiguity.") }
            : [];
        var result = new EvaluationResult(
            "11111111-2222-3333-4444-555555555555", decision, "policy", "1", "sha256:abc", "prompt-v1",
            ["problem_statement", "satisfied_only"], ["satisfied_only"], deficiencies, ambiguities,
            "Trusted engineering summary.", "fake", "scripted");
        return new EvaluationProcessingResult(EvaluationProcessingStatus.Completed, result, null, 1);
    }
}

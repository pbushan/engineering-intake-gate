using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class ManualWorkItemRunServiceTests
{
    [Fact]
    public async Task MAN_003_AC_24_OutsideQueryIsNotEligibleAndNeverEvaluated()
    {
        var source = new FakeSource(new WorkItemQueryResult([41]), Item());
        var evaluation = new CapturingEvaluationService();
        var audit = new CapturingAuditRepository();

        var result = await Service(source, evaluation, audit).ExecuteAsync(42, Configuration());

        Assert.Equal(WorkItemEligibility.NotEligible, result.Eligibility);
        Assert.Equal("OutsideConfiguredSavedQuery", audit.Saved.Single().Evaluation.SelectionReason);
        Assert.Equal(WorkItemEligibility.NotEligible, audit.Saved.Single().Evaluation.Eligibility);
        Assert.Null(result.Decision);
        Assert.Equal(0, result.ProposedMutationCount);
        Assert.Equal(0, source.GetCalls);
        Assert.Equal(0, evaluation.Calls);
    }

    [Fact]
    public async Task DISC_005_AC_14_AC_24_ConfiguredExclusionIsNotEligibleAndNeverEvaluated()
    {
        var item = Item([new RawWorkItemField("Generic.Risk", null, JsonSerializer.SerializeToElement("critical"))]);
        var source = new FakeSource(new WorkItemQueryResult([42]), item);
        var evaluation = new CapturingEvaluationService();
        var audit = new CapturingAuditRepository();

        var result = await Service(source, evaluation, audit).ExecuteAsync(42,
            Configuration([new ExclusionRule("configured-critical", "Generic.Risk", ExclusionOperator.EqualsAny, ["Critical"])]));

        Assert.Equal(WorkItemEligibility.NotEligible, result.Eligibility);
        Assert.Equal("ExcludedByRule:configured-critical", result.ExclusionReason);
        Assert.Equal("9", result.EvaluatedRevision);
        Assert.Equal(0, evaluation.Calls);
        Assert.Empty(audit.Saved.Single().Evaluation.ProposedMutations);
    }

    [Fact]
    public async Task MAN_002_AC_16_EligibleManualItemRunsExistingDryRunPipelineAndAuditsRevision()
    {
        var source = new FakeSource(new WorkItemQueryResult([42]), Item());
        var evaluation = new CapturingEvaluationService();
        var audit = new CapturingAuditRepository();

        var result = await Service(source, evaluation, audit).ExecuteAsync(42, Configuration());

        Assert.Equal(WorkItemEligibility.Eligible, result.Eligibility);
        Assert.Equal(EvaluationProcessingStatus.Completed, result.ProcessingStatus);
        Assert.Equal(IntakeDecision.Pass, result.Decision);
        Assert.Equal(2, result.ProposedMutationCount);
        Assert.Equal(1, evaluation.Calls);
        var persisted = audit.Saved.Single();
        Assert.Equal("9", persisted.Evaluation.EvaluatedRevision);
        Assert.Equal(WorkItemEligibility.Eligible, persisted.Evaluation.Eligibility);
        Assert.Equal(Configuration().Profile.Ado.SavedQueryId, persisted.Evaluation.SavedQueryId);
        Assert.Equal(RunTriggerType.ManualWorkItem, persisted.Run.TriggerType);
        Assert.Equal(ExecutionMode.DryRun, persisted.Run.ExecutionMode);
    }

    [Fact]
    public async Task DISC_001_QueryFailureIsUnknownErrorNotNotEligibleAndDoesNotReadOrEvaluate()
    {
        var source = new FakeSource(
            WorkItemQueryResult.Failed(new(WorkItemReadFailureKind.Transient, "AzureDevOpsServerFailure")), Item());
        var evaluation = new CapturingEvaluationService();
        var audit = new CapturingAuditRepository();

        var result = await Service(source, evaluation, audit).ExecuteAsync(42, Configuration());

        Assert.Equal(WorkItemEligibility.Unknown, result.Eligibility);
        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Equal("AzureDevOpsServerFailure", result.ErrorCategory);
        Assert.Equal(0, source.GetCalls);
        Assert.Equal(0, evaluation.Calls);
        Assert.Equal(0, audit.Saved.Single().Run.SkippedCount);
    }

    [Fact]
    public async Task PHASE9_LiveWithoutWriterIsRefusedBeforeAnyMutation()
    {
        var source = new FakeSource(new WorkItemQueryResult([42]), Item());
        var configuration = Configuration() with
        {
            Profile = Configuration().Profile with
            {
                Processing = Configuration().Profile.Processing with { ExecutionMode = ExecutionMode.Live }
            }
        };

        await Assert.ThrowsAsync<LiveExecutionUnavailableException>(() =>
            Service(source, new CapturingEvaluationService(), new CapturingAuditRepository()).ExecuteAsync(42, configuration));
        Assert.Equal(1, source.QueryCalls);
        Assert.Equal(1, source.GetCalls);
    }

    private static ManualWorkItemRunService Service(
        IWorkItemSource source,
        IIntakeEvaluationService evaluation,
        IAuditRepository audit)
    {
        var run = new IntakeRunService(
            new PassThroughPreprocessor(), evaluation,
            new IntakeDecisionHandler(new IntakeCommentRenderer()), audit,
            new FixedClock(), new NullRunAuditLog());
        return new ManualWorkItemRunService(source, new WorkItemEligibilityEvaluator(), run, audit,
            new FixedClock(), new NullWorkItemReadLog());
    }

    private static RawWorkItem Item(IReadOnlyList<RawWorkItemField>? fields = null) =>
        new("42", "9", "Generic Request", "Title", fields, tags: []);

    private static DeploymentConfiguration Configuration(IReadOnlyList<ExclusionRule>? exclusions = null) => new(
        new DeploymentProfile(
            new ProfileIdentity("profile", "1"),
            new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/generic"), "Project",
                Guid.Parse("11111111-1111-1111-1111-111111111111"), new CredentialReference("ADO_REF")),
            new AiConfiguration("fake", "scripted", new CredentialReference("AI_REF")),
            new ScheduleConfiguration(false, "", "UTC", TimeSpan.FromDays(1)),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0,
                new ContentLimits(1000, 10, 500), new AttachmentLimits(10, 100, 1000, 10)),
            new AuditConfiguration(90), exclusions ?? []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("problem", "Problem", "Problem", CriterionApplicability.Required,
                new NotApplicablePolicy(false, false), "Guidance")]),
        "sha256:abc");

    private sealed class FakeSource(WorkItemQueryResult query, RawWorkItem item) : IWorkItemSource
    {
        public int QueryCalls { get; private set; }
        public int GetCalls { get; private set; }
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return Task.FromResult(query);
        }
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(new WorkItemReadResult(item));
        }
    }

    private sealed class PassThroughPreprocessor : IEvidencePreprocessor
    {
        public EvaluationEvidence Prepare(RawWorkItem workItem, ProcessingConfiguration processing) => new(
            workItem.WorkItemId, workItem.Revision, workItem.WorkItemType, workItem.Title,
            [], "", workItem.Tags, [], [], [],
            new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0,
                workItem.Tags.Count, workItem.Tags.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, false, false, 0),
            new RedactionMetadata(false, 0, new Dictionary<string, int>()));
    }

    private sealed class CapturingEvaluationService : IIntakeEvaluationService
    {
        public int Calls { get; private set; }
        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, string evaluationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationProcessingResult(EvaluationProcessingStatus.Completed,
                new EvaluationResult(evaluationId, IntakeDecision.Pass, "policy", "1", "sha256:abc", EvaluatorPrompt.Version,
                    ["problem"], ["problem"], [], [], "Grounded summary.", "fake", "scripted"), null, 1));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    }

    private sealed class CapturingAuditRepository : IAuditRepository
    {
        public List<(RunAuditRecord Run, EvaluationAuditRecord Evaluation)> Saved { get; } = [];
        public Task SaveAsync(RunAuditRecord run, EvaluationAuditRecord evaluation, CancellationToken cancellationToken = default)
        {
            Saved.Add((run, evaluation));
            return Task.CompletedTask;
        }
        public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
    }
}

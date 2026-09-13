using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class IncrementalRunServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    [Fact]
    public async Task DISC_002_DISC_003_InitialBootstrapUsesConfiguredLookbackAndRegistersOnlyRecentQueryMembers()
    {
        var source = new Source(new WorkItemQueryResult([101, 102]),
            Item(101, Now.AddMinutes(-30)), Item(102, Now.AddHours(-3)));
        var store = new Store();

        var result = await Service(source, store).ExecuteAsync(Configuration(TimeSpan.FromHours(1)), RunTriggerType.ManualIncremental);

        Assert.True(result.Accepted);
        Assert.Equal([101, 102], store.Registrations.Select(item => item.WorkItemId));
        Assert.Equal(DiscoverySelectionReason.NewlyDiscovered, store.Registrations.Single(item => item.WorkItemId == 101).SelectionReason);
        Assert.Equal(RegisteredWorkState.Completed, store.Registrations.Single(item => item.WorkItemId == 102).ProcessingState);
        Assert.Equal(Now, store.Checkpoint!.AdvancedAtUtc);
        Assert.Equal(1, store.Audits.Last().NewlyDiscoveredCount);
        Assert.Equal(2, store.Audits.Last().QueryResultCount);
    }

    [Fact]
    public async Task E8_AC_21_QueryFailureLeavesExistingCheckpointUnchangedAndDoesNotRegisterOrProcess()
    {
        var store = new Store { Checkpoint = new DiscoveryCheckpoint("profile", Now.AddHours(-1), Guid.NewGuid()) };
        var source = new Source(WorkItemQueryResult.Failed(new(WorkItemReadFailureKind.Transient, "AzureDevOpsServerFailure")));

        var result = await Service(source, store).ExecuteAsync(Configuration(TimeSpan.FromHours(1)), RunTriggerType.Scheduled);

        Assert.True(result.Accepted);
        Assert.Equal(IncrementalRunStatus.Error, result.Status);
        Assert.Equal(Now.AddHours(-1), store.Checkpoint!.AdvancedAtUtc);
        Assert.Empty(store.Registrations);
        Assert.Equal("AzureDevOpsServerFailure", store.Audits.Last().ErrorCategory);
        Assert.Equal(AuditActor.System, store.Audits.Last().TriggeredBy);
    }

    [Fact]
    public async Task DISC_008_SuccessfulEmptyQueryCompletesAndAdvancesTheApplicationCheckpoint()
    {
        var store = new Store { Checkpoint = new DiscoveryCheckpoint("profile", Now.AddDays(-1), Guid.NewGuid()) };

        var result = await Service(new Source(new WorkItemQueryResult([])), store)
            .ExecuteAsync(Configuration(TimeSpan.FromHours(1)), RunTriggerType.ManualIncremental);

        Assert.Equal(IncrementalRunStatus.Completed, result.Status);
        Assert.Equal(Now, store.Checkpoint!.AdvancedAtUtc);
        Assert.Equal(0, store.Audits.Last().QueryResultCount);
        Assert.Null(store.Audits.Last().ErrorCategory);
    }

    [Fact]
    public async Task DISC_006_E11_HumanRemovedIncompleteTagDoesNotRequeueHistoricalIncompleteRegistration()
    {
        var store = new Store
        {
            Checkpoint = new DiscoveryCheckpoint("profile", Now.AddDays(-1), Guid.NewGuid()),
            Registrations = [new DiscoveredWorkRegistration("profile", 101, Guid.NewGuid(), Now.AddDays(-1),
                DiscoverySelectionReason.IncompleteReevaluation, RegisteredWorkState.Completed, Now.AddDays(-1))]
        };
        var source = new Source(new WorkItemQueryResult([101]), Item(101, Now.AddMinutes(-5), tags: []));

        await Service(source, store).ExecuteAsync(Configuration(TimeSpan.FromHours(1)), RunTriggerType.ManualIncremental);

        Assert.Equal(RegisteredWorkState.Completed, store.Registrations.Single().ProcessingState);
        Assert.Equal(0, store.Audits.Last().IncompleteReevaluationCount);
        Assert.Equal(0, store.Audits.Last().ProcessedCount);
    }

    private static IncrementalRunService Service(Source source, Store store) => new(
        source, new WorkItemEligibilityEvaluator(),
        new IntakeRunService(new Preprocessor(), new Evaluator(), new IntakeDecisionHandler(new IntakeCommentRenderer()), new Audit(), new Clock(), new NullRunAuditLog()),
        new Audit(), store, new Clock(), new NullWorkItemReadLog());

    private static RawWorkItem Item(int id, DateTimeOffset changed, IReadOnlyList<string>? tags = null) =>
        new(id.ToString(), "1", "Generic", "Title", tags: tags ?? [], changedAtUtc: changed);

    private static DeploymentConfiguration Configuration(TimeSpan lookback) => new(
        new DeploymentProfile(new ProfileIdentity("profile", "1"), new IntakePolicyReference("policy", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"), new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "Project", Guid.NewGuid(), new CredentialReference("ADO")),
            new AiConfiguration("fake", "model", new CredentialReference("AI")), new ScheduleConfiguration(false, "", "UTC", lookback),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0, new ContentLimits(1000, 10, 500), new AttachmentLimits(1, 1, 1, 1)), new AuditConfiguration(1), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"), [new IntakeCriterion("criterion", "Criterion", "", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "")]), "sha256:test");

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Source(WorkItemQueryResult query, params RawWorkItem[] values) : IWorkItemSource
    {
        private readonly Dictionary<int, RawWorkItem> items = values.ToDictionary(item => int.Parse(item.WorkItemId));
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) => Task.FromResult(query);
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.TryGetValue(workItemId, out var item) ? new WorkItemReadResult(item) : WorkItemReadResult.Failed(new(WorkItemReadFailureKind.Permanent, "Missing")));
    }
    private sealed class Preprocessor : IEvidencePreprocessor
    {
        public EvaluationEvidence Prepare(RawWorkItem item, ProcessingConfiguration _) => new(item.WorkItemId, item.Revision, item.WorkItemType, item.Title, [], "", item.Tags, [], [], [],
            new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0), new RedactionMetadata(false, 0, new Dictionary<string, int>()));
        public Task<EvaluationEvidence> PrepareAsync(RawWorkItem item, ProcessingConfiguration processing, CancellationToken cancellationToken = default) => Task.FromResult(Prepare(item, processing));
    }
    private sealed class Evaluator : IIntakeEvaluationService
    {
        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, CancellationToken cancellationToken = default) => EvaluateAsync(evidence, configuration, Guid.NewGuid().ToString("D"), cancellationToken);
        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult(new EvaluationProcessingResult(EvaluationProcessingStatus.Completed,
            new EvaluationResult(evaluationId, IntakeDecision.Pass, "policy", "1", "sha256:test", EvaluatorPrompt.Version, ["criterion"], ["criterion"], [], [], "", "fake", "model"), null, 1));
    }
    private sealed class Audit : IAuditRepository
    {
        public Task SaveAsync(RunAuditRecord run, EvaluationAuditRecord evaluation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
    }
    private sealed class Store : IIncrementalDiscoveryRepository
    {
        public DiscoveryCheckpoint? Checkpoint { get; set; }
        public List<DiscoveredWorkRegistration> Registrations { get; set; } = [];
        public List<IncrementalRunAuditRecord> Audits { get; } = [];
        public Task<DiscoveryCheckpoint?> GetCheckpointAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(Checkpoint);
        public Task<IReadOnlyList<DiscoveredWorkRegistration>> GetRegistrationsAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DiscoveredWorkRegistration>>(Registrations);
        public Task<IReadOnlyList<DiscoveredWorkRegistration>> GetProcessableRegistrationsAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DiscoveredWorkRegistration>>(Registrations.Where(item => item.ProcessingState is RegisteredWorkState.Pending or RegisteredWorkState.Processing or RegisteredWorkState.Error).ToArray());
        public Task RegisterAndAdvanceCheckpointAsync(string profileId, Guid runId, IReadOnlyList<DiscoveryRegistrationRequest> selected, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            foreach (var selection in selected)
            {
                Registrations.RemoveAll(item => item.WorkItemId == selection.WorkItemId);
                Registrations.Add(new DiscoveredWorkRegistration(profileId, selection.WorkItemId, runId, now, selection.Reason, selection.InitialState, now));
            }
            Checkpoint = new DiscoveryCheckpoint(profileId, now, runId);
            return Task.CompletedTask;
        }
        public Task SetProcessingStateAsync(string profileId, int id, RegisteredWorkState state, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var index = Registrations.FindIndex(item => item.WorkItemId == id);
            Registrations[index] = Registrations[index] with { ProcessingState = state, UpdatedAtUtc = now };
            return Task.CompletedTask;
        }
        public Task SaveRunAuditAsync(IncrementalRunAuditRecord record, CancellationToken cancellationToken = default) { Audits.Add(record); return Task.CompletedTask; }
        public Task<IncrementalRunAuditRecord?> GetRunAuditAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(Audits.LastOrDefault(item => item.RunId == runId));
        public Task<ActiveRunLease?> TryAcquireRunLeaseAsync(string profileId, Guid runId, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult<ActiveRunLease?>(new(runId, now, now + duration));
        public Task<bool> RefreshRunLeaseAsync(string profileId, Guid runId, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ReleaseRunLeaseAsync(string profileId, Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

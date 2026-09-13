using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using IntakeGate.Application.Evidence;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class MutationReconciliationServiceTests
{
    [Fact]
    public async Task AC_07_E10_PendingCommentIsReconciledWithoutEvaluationOrDuplicateTag()
    {
        var source = new MutableSource("8", ["VALID"]);
        var writer = new MutableWriter(source) { FailCommentsRemaining = 0 };
        var repository = new Reconciliations(Pending());
        var service = new MutationReconciliationService(source, writer, repository, new Audit(), new Clock(), new NoDelay());

        await service.ReconcilePendingAsync(Configuration());
        await service.ReconcilePendingAsync(Configuration());

        Assert.Equal(0, writer.TagWrites);
        Assert.Equal(1, writer.CommentWrites);
        Assert.Equal(ReconciliationStatus.Completed, repository.Record.Status);
        Assert.Contains("evaluationId=11111111-2222-3333-4444-555555555555", source.Comments.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task E10_UncertainCommentOutcomeUsesMarkerInsteadOfReposting()
    {
        var pending = Pending();
        var source = new MutableSource("8", ["VALID"], [pending.IntendedMutations.Single(item => item.Type == ProposedMutationType.PostComment).Body!]);
        var writer = new MutableWriter(source);
        var repository = new Reconciliations(pending);
        var service = new MutationReconciliationService(source, writer, repository, new Audit(), new Clock(), new NoDelay());

        await service.ReconcilePendingAsync(Configuration());

        Assert.Equal(0, writer.CommentWrites);
        Assert.Equal(ReconciliationStatus.Completed, repository.Record.Status);
    }

    [Fact]
    public async Task AC_20_StaleReconciliationNeverAppliesOldComment()
    {
        var source = new MutableSource("9", ["VALID"]);
        var writer = new MutableWriter(source);
        var repository = new Reconciliations(Pending());
        var service = new MutationReconciliationService(source, writer, repository, new Audit(), new Clock(), new NoDelay());

        await service.ReconcilePendingAsync(Configuration());

        Assert.Equal(0, writer.CommentWrites);
        Assert.Equal(ReconciliationStatus.StaleReevaluationRequired, repository.Record.Status);
    }

    [Fact]
    public async Task ERR_002_TransientCommentFailureUsesBoundedRetry()
    {
        var source = new MutableSource("8", ["VALID"]);
        var writer = new MutableWriter(source) { FailCommentsRemaining = 1 };
        var repository = new Reconciliations(Pending());
        var delay = new NoDelay();
        var service = new MutationReconciliationService(source, writer, repository, new Audit(), new Clock(), delay);

        await service.ReconcilePendingAsync(Configuration(retries: 1));

        Assert.Equal(2, writer.CommentWrites);
        Assert.Equal(1, delay.Count);
        Assert.Equal(ReconciliationStatus.Completed, repository.Record.Status);
    }

    private static MutationReconciliationRecord Pending()
    {
        var marker = "<!-- engineering-intake-gate:validatorVersion=1;evaluationId=11111111-2222-3333-4444-555555555555 -->";
        return new MutationReconciliationRecord("11111111-2222-3333-4444-555555555555", "profile", "42", "7", "8",
            [ProposedMutation.AddTag("VALID"), ProposedMutation.PostComment("Result\n\n" + marker, marker)], ["VALID"],
            [ProposedMutationType.AddTag], ReconciliationStatus.Pending, DateTimeOffset.Parse("2026-09-10T00:00:00Z"), DateTimeOffset.Parse("2026-09-10T00:00:00Z"), 1, null);
    }

    private static DeploymentConfiguration Configuration(int retries = 2) => new(
        new DeploymentProfile(new ProfileIdentity("profile", "1"), new IntakePolicyReference("x", new Uri("https://example.invalid")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"), new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "p", Guid.NewGuid(), new CredentialReference("x")),
            new AiConfiguration("fake", "fake", new CredentialReference("x")), new ScheduleConfiguration(false, "", "UTC", TimeSpan.FromDays(1)),
            new ProcessingConfiguration(ExecutionMode.Live, 1, retries, new ContentLimits(1, 1, 1), new AttachmentLimits(1, 1, 1, 1)), new AuditConfiguration(1), []),
        new IntakePolicy(new PolicyIdentity("p", "1"), []), "sha256:x");

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-10T01:00:00Z"); }
    private sealed class NoDelay : IRetryDelay { public int Count { get; private set; } public Task DelayAsync(TimeSpan _, CancellationToken __ = default) { Count++; return Task.CompletedTask; } }

    private sealed class Reconciliations(MutationReconciliationRecord record) : IReconciliationRepository
    {
        public MutationReconciliationRecord Record { get; private set; } = record;
        public Task CreatePendingAsync(MutationReconciliationRecord value, CancellationToken cancellationToken = default) { Record = value; return Task.CompletedTask; }
        public Task<IReadOnlyList<MutationReconciliationRecord>> GetPendingAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MutationReconciliationRecord>>(Record.Status == ReconciliationStatus.Pending ? [Record] : []);
        public Task UpdateAsync(MutationReconciliationRecord value, CancellationToken cancellationToken = default) { Record = value; return Task.CompletedTask; }
    }

    private sealed class Audit : IAuditRepository
    {
        public Task SaveAsync(RunAuditRecord run, EvaluationAuditRecord evaluation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
    }

    private sealed class MutableSource(string revision, IReadOnlyList<string> tags, IReadOnlyList<string>? comments = null) : IWorkItemSource
    {
        public string Revision { get; set; } = revision;
        public List<string> Tags { get; } = tags.ToList();
        public List<string> Comments { get; } = (comments ?? []).ToList();
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkItemQueryResult([42]));
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default) => Task.FromResult(new WorkItemReadResult(
            new RawWorkItem("42", Revision, "Generic", "Title", tags: Tags, comments: Comments.Select((body, index) => new RawWorkItemComment(index.ToString(), null, null, body)).ToArray())));
    }

    private sealed class MutableWriter(MutableSource source) : IWorkItemWriter
    {
        public int TagWrites { get; private set; }
        public int CommentWrites { get; private set; }
        public int FailCommentsRemaining { get; set; }
        public Task<WorkItemMutationResult> UpdateIntakeTagsAsync(IntakeTagUpdateRequest request, CancellationToken cancellationToken = default)
        { TagWrites++; source.Tags.Clear(); source.Tags.AddRange(request.FinalTags); source.Revision = (int.Parse(source.Revision) + 1).ToString(); return Task.FromResult(WorkItemMutationResult.Success(source.Revision)); }
        public Task<WorkItemMutationResult> AddValidatorCommentAsync(ValidatorCommentRequest request, CancellationToken cancellationToken = default)
        { CommentWrites++; if (FailCommentsRemaining-- > 0) return Task.FromResult(WorkItemMutationResult.Failed(WorkItemMutationFailureKind.Transient, "AzureDevOpsMutationUnavailable", 429)); source.Comments.Add(request.Body); return Task.FromResult(WorkItemMutationResult.Success(null)); }
    }
}

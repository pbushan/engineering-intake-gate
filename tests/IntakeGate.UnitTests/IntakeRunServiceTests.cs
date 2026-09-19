using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using System.Text.Json;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class IntakeRunServiceTests
{
    [Fact]
    public async Task DRY_001_DRY_004_DryRunPersistsProposalsWithoutTreatingThemAsCurrentState()
    {
        var repository = new CapturingAuditRepository();
        var service = Service(repository);
        var raw = new RawWorkItem("42", "7", "Generic", "Title", tags: ["ExistingTag"]);
        var originalTags = raw.Tags.ToArray();

        var first = await service.ExecuteAsync(raw, Configuration(), RunTriggerType.ManualWorkItem, "fixture");
        var second = await service.ExecuteAsync(raw, Configuration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal(EvaluationProcessingStatus.Completed, first.ProcessingStatus);
        Assert.Equal(ExecutionMode.DryRun, first.ExecutionMode);
        Assert.Contains(first.ProposedMutations, item => item is { Type: ProposedMutationType.AddTag, Tag: "VALID" });
        Assert.Empty(first.AttemptedMutations);
        Assert.Empty(first.MutationOutcomes);
        Assert.Equal(originalTags, raw.Tags);
        Assert.Contains(second.ProposedMutations, item => item is { Type: ProposedMutationType.AddTag, Tag: "VALID" });
        Assert.Equal(2, repository.Saved.Count);
        Assert.All(repository.Saved, pair =>
        {
            Assert.Empty(pair.Evaluation.AttemptedMutations);
            Assert.Empty(pair.Evaluation.MutationOutcomes);
            Assert.Contains(pair.Evaluation.ProposedMutations, item => item.Type == ProposedMutationType.AddTag);
            Assert.Contains($"evaluationId={pair.Evaluation.EvaluationId} -->",
                pair.Evaluation.ProposedMutations.Single(item => item.Type == ProposedMutationType.PostComment).CommentMarker,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SAFE_001_AuditFailureReturnsErrorAndNoAttemptedOrAppliedMutation()
    {
        var result = await Service(new CapturingAuditRepository { ThrowOnSave = true }).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), Configuration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Equal("AuditPersistenceFailure", result.ErrorCategory);
        Assert.Empty(result.ProposedMutations);
        Assert.Empty(result.AttemptedMutations);
        Assert.Empty(result.MutationOutcomes);
    }

    [Fact]
    public async Task SAFE_001_LiveModeExplicitlyFailsBeforeEvaluationOrMutation()
    {
        var configuration = Configuration();
        configuration = configuration with
        {
            Profile = configuration.Profile with
            {
                Processing = configuration.Profile.Processing with { ExecutionMode = ExecutionMode.Live }
            }
        };

        await Assert.ThrowsAsync<LiveExecutionUnavailableException>(() => Service(new CapturingAuditRepository()).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), configuration, RunTriggerType.ManualWorkItem, "fixture"));
    }

    [Fact]
    public async Task AC_06_RepeatedTransientProviderFailureIsAuditedAsErrorWithNoMutations()
    {
        var repository = new CapturingAuditRepository();
        var provider = new FakeIntakeAiProvider(Enumerable.Repeat<Func<EvaluationRequest, AiProviderResponse>>(
            _ => AiProviderResponse.Failed(new(AiProviderFailureKind.Transient, "ProviderServerFailure")), 3));
        var result = await Service(repository, provider).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), Configuration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Null(result.Decision);
        Assert.Empty(result.ProposedMutations);
        Assert.Equal("ProviderServerFailure", repository.Saved.Single().Evaluation.ErrorCategories.Single());
    }

    [Fact]
    public async Task AC_06_PermanentProviderFailureDoesNotRetryAndIsAuditedSafely()
    {
        var repository = new CapturingAuditRepository();
        var calls = 0;
        var provider = new FakeIntakeAiProvider([_ =>
        {
            calls++;
            return AiProviderResponse.Failed(new(AiProviderFailureKind.Permanent, "ProviderAuthenticationFailure"));
        }]);
        var result = await Service(repository, provider).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), Configuration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal(1, calls);
        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Empty(result.ProposedMutations);
        Assert.Equal("ProviderAuthenticationFailure", repository.Saved.Single().Run.ErrorCategory);
    }

    [Fact]
    public async Task AUD_001_AUD_002_NFR_008_RetryUsageIsSummedOnceAndCostAggregatesAtEvaluationAndRun()
    {
        var repository = new CapturingAuditRepository();
        var provider = new FakeIntakeAiProvider([
            _ => AiProviderResponse.Failed(new(AiProviderFailureKind.Transient, "ProviderServerFailure"),
                new AiProviderAttemptMetadata("req-1", "reported-model", new TokenUsage(10, 2, 12))),
            request => FakeIntakeAiProvider.ValidPass(request) with
            {
                Metadata = new AiProviderAttemptMetadata("req-2", "reported-model", new TokenUsage(20, 4, 24))
            }
        ]);

        var result = await Service(repository, provider).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), Configuration(withPricing: true), RunTriggerType.ManualWorkItem, "fixture");
        var saved = repository.Saved.Single();

        Assert.Equal(EvaluationProcessingStatus.Completed, result.ProcessingStatus);
        Assert.Equal(new TokenUsage(30, 6, 36), saved.Evaluation.TokenUsage);
        Assert.Equal(saved.Evaluation.TokenUsage, saved.Run.TokenUsage);
        Assert.Equal(0.00009m, saved.Evaluation.EstimatedCost!.Amount);
        Assert.Equal(saved.Evaluation.EstimatedCost, saved.Run.EstimatedCost);
        Assert.Equal(2, saved.Evaluation.AiInteractions.Count);
        Assert.All(saved.Evaluation.AiInteractions,
            interaction => Assert.NotNull(interaction.EstimatedTotalCost));
        Assert.Equal(["req-1", "req-2"], saved.Evaluation.ProviderRequestIds);
        Assert.Equal("reported-model", saved.Evaluation.ProviderReportedModel);
    }

    [Fact]
    public async Task CNT_007_AUD_001_AttachmentProcessingMetadataButNoBytesOrExtractedContentIsAudited()
    {
        var repository = new CapturingAuditRepository();
        var secretBytes = new byte[] { 11, 22, 33, 44, 55 };
        var evidence = new EvaluationEvidence("42", "7", "Generic", "Title", [], "", [], [], [],
            [new EvaluationAttachmentMetadata("a-1", "large.pdf", "application/pdf", 5000, true,
                AttachmentProcessingStatus.Partial, AttachmentInspectionMode.PdfText, "secret-bearing extracted content",
                true, 120, 10, "PdfPageLimitExceeded")],
            new EvidenceProcessingDisclosure(true, false, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, true, true, 20),
            new RedactionMetadata(false, 0, new Dictionary<string, int>()))
        {
            VisualEvidence = [new VisualEvidence("a-2", "shot.png", "image/png", secretBytes)]
        };
        var service = new IntakeRunService(
            new FixedEvidencePreprocessor(evidence), new StubEvaluationService(),
            new IntakeDecisionHandler(new IntakeCommentRenderer()), repository,
            new FixedClock(), new NullRunAuditLog());

        await service.ExecuteAsync(new RawWorkItem("42", "7", "Generic", "Title"), Configuration(), RunTriggerType.ManualWorkItem, "fixture");

        var audit = repository.Saved.Single().Evaluation;
        var metadata = Assert.Single(audit.AttachmentProcessing);
        Assert.Equal(AttachmentProcessingStatus.Partial, metadata.ProcessingStatus);
        Assert.Equal(120, metadata.PagesAvailable);
        Assert.Equal(10, metadata.PagesInspected);
        var serialized = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("secret-bearing extracted content", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(secretBytes), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SAFE_001_AC_20_E1_StaleRevisionAfterEvaluationCausesZeroWritesAndAuditsRequeue()
    {
        var audit = new LiveAuditRepository();
        var source = new SequencedSource(new RawWorkItem("42", "8", "Generic", "Title"));
        var writer = new RecordingWriter();
        var result = await LiveService(audit, source, writer).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), LiveConfiguration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal("StaleRevisionReevaluationRequired", result.ErrorCategory);
        Assert.Equal(0, writer.TagWrites);
        Assert.Equal(0, writer.CommentWrites);
        Assert.Equal(MutationExecutionState.StaleReevaluationRequired, audit.Evaluation!.MutationState);
        Assert.Empty(audit.Evaluation.AttemptedMutations);
    }

    [Fact]
    public async Task AC_01_SAFE_002_LivePassUsesOneRevisionGuardedTagWriteThenOneMarkedComment()
    {
        var audit = new LiveAuditRepository();
        var source = new SequencedSource(
            new RawWorkItem("42", "7", "Generic", "Title"),
            new RawWorkItem("42", "8", "Generic", "Title", tags: ["VALID"]));
        var writer = new RecordingWriter();
        var result = await LiveService(audit, source, writer).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), LiveConfiguration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Null(result.ErrorCategory);
        Assert.Equal(1, writer.TagWrites);
        Assert.Equal(1, writer.CommentWrites);
        Assert.Equal("7", writer.TagRequest!.ExpectedRevision);
        Assert.Contains("VALID", writer.TagRequest.FinalTags);
        Assert.Contains("engineering-intake-gate:validatorVersion=1", writer.CommentRequest!.Body, StringComparison.Ordinal);
        Assert.Equal(2, audit.Evaluation!.AttemptedMutations.Count);
        Assert.All(audit.Evaluation.MutationOutcomes, outcome => Assert.True(outcome.Succeeded));
    }

    [Fact]
    public async Task ERR_004_AC_22_E9_PersistenceFailureAfterEvaluationBlocksAllLiveWrites()
    {
        var audit = new LiveAuditRepository { ThrowOnCreateReconciliation = true };
        var source = new SequencedSource(new RawWorkItem("42", "7", "Generic", "Title"));
        var writer = new RecordingWriter();

        var result = await LiveService(audit, source, writer).ExecuteAsync(
            new RawWorkItem("42", "7", "Generic", "Title"), LiveConfiguration(), RunTriggerType.ManualWorkItem, "fixture");

        Assert.Equal(EvaluationProcessingStatus.Error, result.ProcessingStatus);
        Assert.Equal("PersistenceUnavailable", result.ErrorCategory);
        Assert.Equal(0, writer.TagWrites);
        Assert.Equal(0, writer.CommentWrites);
    }

    private static IntakeRunService Service(IAuditRepository repository) => new(
        new StubPreprocessor(), new StubEvaluationService(),
        new IntakeDecisionHandler(new IntakeCommentRenderer()), repository,
        new FixedClock(), new NullRunAuditLog());

    private static IntakeRunService Service(IAuditRepository repository, IIntakeAiProvider provider) => new(
        new StubPreprocessor(),
        new IntakeEvaluationService(new EvaluationRequestBuilder(), provider, new EvaluationResponseParser(), new EvaluationContractValidator(), new NullEvaluationLog()),
        new IntakeDecisionHandler(new IntakeCommentRenderer()), repository, new FixedClock(), new NullRunAuditLog(), new StubCostAccounting());

    private static IntakeRunService LiveService(LiveAuditRepository repository, IWorkItemSource source, IWorkItemWriter writer) => new(
        new StubPreprocessor(), new StubEvaluationService(), new IntakeDecisionHandler(new IntakeCommentRenderer()),
        repository, new FixedClock(), new NullRunAuditLog(), new StubCostAccounting(), source, writer);

    private static DeploymentConfiguration LiveConfiguration() => Configuration() with
    {
        Profile = Configuration().Profile with { Processing = Configuration().Profile.Processing with { ExecutionMode = ExecutionMode.Live } }
    };

    private static DeploymentConfiguration Configuration(bool withPricing = false) => new(
        new DeploymentProfile(
            new ProfileIdentity("profile", "1"),
            new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "project", Guid.NewGuid(), new CredentialReference("ADO_REF")),
            new AiConfiguration("fake", "scripted", new CredentialReference("AI_REF"))
            {
                Pricing = withPricing ? [new ModelPricing("fake", "scripted", 1m, 10m, "USD", "synthetic-v1")] : []
            },
            new ScheduleConfiguration(false, string.Empty, "UTC", TimeSpan.FromDays(1)),
            new ProcessingConfiguration(ExecutionMode.DryRun, 1, 2, new ContentLimits(1000, 10, 500), new AttachmentLimits(10, 100, 1000, 10)),
            new AuditConfiguration(90), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("problem", "Problem", "Problem", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Guidance")]),
        "sha256:abc");

    private sealed class StubPreprocessor : IEvidencePreprocessor
    {
        public EvaluationEvidence Prepare(RawWorkItem workItem, ProcessingConfiguration processing) => new(
            workItem.WorkItemId, workItem.Revision, workItem.WorkItemType, workItem.Title, [], string.Empty, workItem.Tags,
            [], [], [], new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0,
                workItem.Tags.Count, workItem.Tags.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                false, false, 0), new RedactionMetadata(false, 0, new Dictionary<string, int>()));
    }

    private sealed class FixedEvidencePreprocessor(EvaluationEvidence evidence) : IEvidencePreprocessor
    {
        public EvaluationEvidence Prepare(RawWorkItem workItem, ProcessingConfiguration processing) => evidence;
    }

    private sealed class StubEvaluationService : IIntakeEvaluationService
    {
        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EvaluationProcessingResult> EvaluateAsync(EvaluationEvidence evidence, DeploymentConfiguration configuration, string evaluationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationProcessingResult(EvaluationProcessingStatus.Completed,
                new EvaluationResult(evaluationId, IntakeDecision.Pass, "policy", "1", "sha256:abc", EvaluatorPrompt.Version,
                    ["problem"], ["problem"], [], [], "Grounded summary.", "fake", "scripted"), null, 1));
    }

    private sealed class StubCostAccounting : IAiCostAccountingService
    {
        public Task<AiCostAccountingResult> EstimateAsync(
            string configuredProvider,
            string configuredModel,
            IReadOnlyList<AiProviderInteractionUsage> interactions,
            CancellationToken cancellationToken = default)
        {
            var records = interactions.Select(interaction =>
            {
                if (interaction.TokenUsage is not { } usage)
                    return new AiInteractionCostRecord(interaction.Attempt,
                        interaction.RequestedProviderIdentifier, interaction.RequestedModelIdentifier,
                        configuredProvider, interaction.ProviderReportedModel ?? configuredModel,
                        interaction.ProviderRequestId, null, null, null, null, null);
                var input = usage.InputTokens / 1_000_000m;
                var output = usage.OutputTokens / 1_000_000m * 10m;
                return new AiInteractionCostRecord(interaction.Attempt,
                    interaction.RequestedProviderIdentifier, interaction.RequestedModelIdentifier,
                    configuredProvider, interaction.ProviderReportedModel ?? configuredModel,
                    interaction.ProviderRequestId, usage, input, output, input + output, null);
            }).ToArray();
            var known = records.Where(record => record.EstimatedTotalCost is not null).ToArray();
            EstimatedCost? total = known.Length == 0 ? null : new EstimatedCost(
                known.Sum(record => record.EstimatedTotalCost!.Value), "USD")
            {
                Complete = known.Length == records.Length,
                PricedInteractions = known.Length,
                TotalInteractions = records.Length,
                PricingIdentity = "synthetic-v1"
            };
            return Task.FromResult(new AiCostAccountingResult(total, records));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    }

    private sealed class CapturingAuditRepository : IAuditRepository
    {
        public bool ThrowOnSave { get; init; }
        public List<(RunAuditRecord Run, EvaluationAuditRecord Evaluation)> Saved { get; } = [];
        public Task SaveAsync(RunAuditRecord run, EvaluationAuditRecord evaluation, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave) throw new InvalidOperationException("Synthetic persistence failure.");
            Saved.Add((run, evaluation));
            return Task.CompletedTask;
        }
        public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<EvaluationAuditRecord?>(null);
    }

    private sealed class LiveAuditRepository : IAuditRepository, IReconciliationRepository
    {
        public bool ThrowOnCreateReconciliation { get; init; }
        public EvaluationAuditRecord? Evaluation { get; private set; }
        public Task SaveAsync(RunAuditRecord run, EvaluationAuditRecord evaluation, CancellationToken cancellationToken = default)
        {
            Evaluation = evaluation;
            return Task.CompletedTask;
        }
        public Task UpdateMutationAuditAsync(string evaluationId, IReadOnlyList<ProposedMutation> attempted, IReadOnlyList<MutationOutcome> outcomes,
            MutationExecutionState state, IReadOnlyList<string> errors, CancellationToken cancellationToken = default)
        {
            Evaluation = Evaluation! with { AttemptedMutations = attempted, MutationOutcomes = outcomes, MutationState = state, ErrorCategories = errors };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EvaluationAuditRecord>> GetWorkItemHistoryAsync(string workItemId, string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EvaluationAuditRecord>>([]);
        public Task<RunAuditRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunAuditRecord?>(null);
        public Task<EvaluationAuditRecord?> GetEvaluationAsync(string evaluationId, CancellationToken cancellationToken = default) => Task.FromResult(Evaluation);
        public Task<EvaluationAuditRecord?> GetEvaluationForRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(Evaluation);
        public Task CreatePendingAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCreateReconciliation) throw new InvalidOperationException("Synthetic persistence failure.");
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MutationReconciliationRecord>> GetPendingAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MutationReconciliationRecord>>([]);
        public Task UpdateAsync(MutationReconciliationRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SequencedSource(params RawWorkItem[] items) : IWorkItemSource
    {
        private readonly Queue<RawWorkItem> items = new(items);
        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkItemQueryResult([42]));
        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemReadResult(items.Dequeue()));
    }

    private sealed class RecordingWriter : IWorkItemWriter
    {
        public int TagWrites { get; private set; }
        public int CommentWrites { get; private set; }
        public IntakeTagUpdateRequest? TagRequest { get; private set; }
        public ValidatorCommentRequest? CommentRequest { get; private set; }
        public Task<WorkItemMutationResult> UpdateIntakeTagsAsync(IntakeTagUpdateRequest request, CancellationToken cancellationToken = default)
        {
            TagWrites++; TagRequest = request;
            return Task.FromResult(WorkItemMutationResult.Success("8", 200));
        }
        public Task<WorkItemMutationResult> AddValidatorCommentAsync(ValidatorCommentRequest request, CancellationToken cancellationToken = default)
        {
            CommentWrites++; CommentRequest = request;
            return Task.FromResult(WorkItemMutationResult.Success(null, 200));
        }
    }
}

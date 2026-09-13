using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

/// <summary>
/// Phase 12 release-gate scenarios that exercise the governed HTTP endpoint, real evidence and
/// evaluation pipeline, deterministic decision mapping, SQLite audit, and a stateful ADO double.
/// Test display names preserve the locked GIVEN/WHEN/THEN acceptance form in runner output.
/// </summary>
public sealed class MvpMutationAcceptanceTests
{
    [Fact(DisplayName = "AC-02 GIVEN an incomplete eligible ticket WHEN LIVE evaluation returns FAIL THEN incomplete state and one marked comment are enforced")]
    public async Task AC_02_IncompleteTicketFailsAndEnforcesOnlyConfiguredIntakeState()
    {
        var source = new MutableSource();
        var provider = Repeating(FakeIntakeAiProvider.ValidFail);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);

        var result = await RunAsync(fixture.Client);

        Assert.Equal("fail", result.GetProperty("decision").GetString());
        Assert.Contains("INTAKE-INCOMPLETE", source.Tags);
        Assert.DoesNotContain("INTAKE-VALIDATED", source.Tags);
        Assert.Single(source.ValidatorComments);
        Assert.Contains("engineering-intake-gate:validatorVersion=1", source.ValidatorComments[0], StringComparison.Ordinal);
        Assert.Equal(1, fixture.Writer.TagWrites);
        Assert.Equal(1, fixture.Writer.CommentWrites);
    }

    [Fact(DisplayName = "AC-03 GIVEN a previously failed ticket is corrected WHEN its next evaluation passes THEN FAIL transitions to PASS without deleting history")]
    public async Task AC_03_CorrectedTicketTransitionsFromFailToPassAndRetainsHistory()
    {
        var source = new MutableSource();
        var provider = new CountingProvider([
            FakeIntakeAiProvider.ValidFail,
            FakeIntakeAiProvider.ValidPass
        ]);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);

        var failed = await RunAsync(fixture.Client);
        var passed = await RunAsync(fixture.Client);

        Assert.Equal("fail", failed.GetProperty("decision").GetString());
        Assert.Equal("pass", passed.GetProperty("decision").GetString());
        Assert.Contains("INTAKE-VALIDATED", source.Tags);
        Assert.DoesNotContain("INTAKE-INCOMPLETE", source.Tags);
        Assert.Equal(2, source.ValidatorComments.Count);
        Assert.Contains("intake incomplete", source.ValidatorComments[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intake validated", source.ValidatorComments[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "AC-04 GIVEN unchanged normalized FAIL gaps WHEN the ticket is evaluated again THEN no duplicate comment or tag write occurs")]
    public async Task AC_04_UnchangedFailureDoesNotDuplicateCommentOrTagMutation()
    {
        var source = new MutableSource();
        await using var fixture = await LiveFixture.CreateAsync(source, Repeating(FakeIntakeAiProvider.ValidFail));

        await RunAsync(fixture.Client);
        var tagsAfterFirstRun = source.Tags.ToArray();
        await RunAsync(fixture.Client);

        Assert.Equal(tagsAfterFirstRun, source.Tags);
        Assert.Single(source.ValidatorComments);
        Assert.Equal(1, fixture.Writer.TagWrites);
        Assert.Equal(1, fixture.Writer.CommentWrites);
    }

    [Fact(DisplayName = "AC-05 GIVEN materially changed FAIL gaps WHEN the ticket is evaluated again THEN a new current-gap comment is created")]
    public async Task AC_05_ChangedDeficienciesCreateANewCommentWithoutNoOpTagWrite()
    {
        var source = new MutableSource();
        var provider = new CountingProvider([
            request => FailureFor(request, request.Criteria[0].Id),
            request => FailureFor(request, request.Criteria[1].Id)
        ]);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);

        await RunAsync(fixture.Client);
        await RunAsync(fixture.Client);

        Assert.Equal(2, source.ValidatorComments.Count);
        Assert.NotEqual(source.ValidatorComments[0], source.ValidatorComments[1]);
        Assert.Equal(1, fixture.Writer.TagWrites);
        Assert.Equal(2, fixture.Writer.CommentWrites);
    }

    [Fact(DisplayName = "E11 GIVEN a human removed the incomplete tag WHEN unchanged historical failure state is inspected THEN the application does not fight the human without a new evaluation")]
    public async Task E11_HumanRemovalIsAuthoritativeUntilANewNormalEvaluation()
    {
        var source = new MutableSource();
        var provider = Repeating(FakeIntakeAiProvider.ValidFail);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);

        await RunIncrementalAsync(fixture.Client);
        Assert.Contains("INTAKE-INCOMPLETE", source.Tags);
        source.Tags.Remove("INTAKE-INCOMPLETE");
        var callsBefore = provider.Calls;
        var writesBefore = fixture.Writer.TagWrites;

        await RunIncrementalAsync(fixture.Client);

        Assert.DoesNotContain("INTAKE-INCOMPLETE", source.Tags);
        Assert.Equal(callsBefore, provider.Calls);
        Assert.Equal(writesBefore, fixture.Writer.TagWrites);
    }

    [Fact(DisplayName = "E12 GIVEN a human added the validated tag WHEN an eligible manual validation is requested THEN the tag is not treated as proof and evaluation still runs")]
    public async Task E12_ManualValidatedTagDoesNotSuppressLegitimateEvaluation()
    {
        var source = new MutableSource(tags: ["unrelated", "INTAKE-VALIDATED"]);
        var provider = Repeating(FakeIntakeAiProvider.ValidPass);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);

        var result = await RunAsync(fixture.Client);

        Assert.Equal("pass", result.GetProperty("decision").GetString());
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, fixture.Writer.TagWrites);
        Assert.Equal(1, fixture.Writer.CommentWrites);
    }

    [Fact(DisplayName = "E13 GIVEN both intake tags are present WHEN a trusted PASS is enforced THEN the contradictory tag is removed and unrelated tags survive")]
    public async Task E13_ContradictoryIntakeTagsConvergeToTheTrustedDecision()
    {
        var source = new MutableSource(tags: ["unrelated", "INTAKE-VALIDATED", "INTAKE-INCOMPLETE"]);
        await using var fixture = await LiveFixture.CreateAsync(source, Repeating(FakeIntakeAiProvider.ValidPass));

        await RunAsync(fixture.Client);

        Assert.Equal(["INTAKE-VALIDATED", "unrelated"], source.Tags.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(1, fixture.Writer.TagWrites);
    }

    [Fact(DisplayName = "E14 GIVEN a validator-like comment has a malformed marker WHEN evidence is prepared THEN it remains human evidence and is not silently discarded")]
    public async Task E14_MalformedValidatorMarkerIsNotTreatedAsApplicationHistory()
    {
        await using var fixture = await LiveFixture.CreateAsync(new MutableSource(), Repeating(FakeIntakeAiProvider.ValidPass));
        var malformed = "Human clarification <!-- engineering-intake-gate:validatorVersion=1;evaluationId=not-a-guid -->";
        var raw = new RawWorkItem("42", "7", "Generic", "Title", comments:
            [new RawWorkItemComment("c-1", "Person", DateTimeOffset.Parse("2026-09-10T00:00:00Z"), malformed)]);

        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evidence/prepare", raw);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Human clarification", body, StringComparison.Ordinal);
        Assert.Contains("not-a-guid", body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "E15 GIVEN a previously failed ticket leaves the saved query WHEN manual validation is requested THEN it is NOT_ELIGIBLE and historical state is untouched")]
    public async Task E15_ItemLeavingQueryStopsEvaluationWithoutRemovingHistoricalState()
    {
        var source = new MutableSource();
        var provider = Repeating(FakeIntakeAiProvider.ValidFail);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);
        await RunAsync(fixture.Client);
        var tags = source.Tags.ToArray();
        var comments = source.ValidatorComments.ToArray();
        source.QueryMember = false;
        var calls = provider.Calls;

        var result = await RunAsync(fixture.Client);

        Assert.Equal("notEligible", result.GetProperty("eligibility").GetString());
        Assert.Equal(calls, provider.Calls);
        Assert.Equal(tags, source.Tags);
        Assert.Equal(comments, source.ValidatorComments);
    }

    [Fact(DisplayName = "E16 GIVEN a previously failed ticket becomes excluded WHEN manual validation is requested THEN it is NOT_ELIGIBLE and historical state is untouched")]
    public async Task E16_ItemBecomingExcludedStopsEvaluationWithoutRemovingHistoricalState()
    {
        var source = new MutableSource();
        var provider = Repeating(FakeIntakeAiProvider.ValidFail);
        await using var fixture = await LiveFixture.CreateAsync(source, provider);
        await RunAsync(fixture.Client);
        var tags = source.Tags.ToArray();
        var comments = source.ValidatorComments.ToArray();
        source.Excluded = true;
        var calls = provider.Calls;

        var result = await RunAsync(fixture.Client);

        Assert.Equal("notEligible", result.GetProperty("eligibility").GetString());
        Assert.Equal("ExcludedByRule:excluded-state", result.GetProperty("exclusionReason").GetString());
        Assert.Equal(calls, provider.Calls);
        Assert.Equal(tags, source.Tags);
        Assert.Equal(comments, source.ValidatorComments);
    }

    private static CountingProvider Repeating(Func<EvaluationRequest, AiProviderResponse> response) => new([], response);

    private static AiProviderResponse FailureFor(EvaluationRequest request, string criterionId) =>
        FakeIntakeAiProvider.Json(request, "FAIL", [criterionId], [],
            [new { criterionId, reason = $"{criterionId} is insufficient.", requiredSupportAction = $"Provide {criterionId}." }],
            [], "Known evidence is retained while the current gap is corrected.");

    private static async Task<JsonElement> RunAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/runs/work-items/42", null);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> RunIncrementalAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/runs", null);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private sealed class CountingProvider(
        IEnumerable<Func<EvaluationRequest, AiProviderResponse>> responses,
        Func<EvaluationRequest, AiProviderResponse>? repeat = null) : IIntakeAiProvider
    {
        private readonly Queue<Func<EvaluationRequest, AiProviderResponse>> responses = new(responses);
        public int Calls { get; private set; }

        public Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var response = responses.Count > 0 ? responses.Dequeue() : repeat ?? throw new InvalidOperationException("No scripted response remains.");
            return Task.FromResult(response(request));
        }
    }

    private sealed class MutableSource(IReadOnlyCollection<string>? tags = null) : IWorkItemSource
    {
        public int Revision { get; set; } = 7;
        public bool QueryMember { get; set; } = true;
        public bool Excluded { get; set; }
        public List<string> Tags { get; } = (tags ?? ["unrelated"]).ToList();
        public List<string> ValidatorComments { get; } = [];

        public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemQueryResult(QueryMember ? [42] : []));

        public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemReadResult(new RawWorkItem(
                "42", Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), "Generic Request", "Synthetic acceptance ticket",
                fields: Excluded
                    ? [new RawWorkItemField("Example.State", "State", JsonSerializer.SerializeToElement("Excluded"))]
                    : [], description: "Useful investigation context.", tags: Tags,
                comments: ValidatorComments.Select((body, index) => new RawWorkItemComment(
                    $"validator-{index}", "Synthetic Validator", DateTimeOffset.Parse("2026-09-10T00:00:00Z").AddMinutes(index), body)).ToArray(),
                changedAtUtc: DateTimeOffset.Parse("2026-09-10T12:00:00Z"))));
    }

    private sealed class MutableWriter(MutableSource source) : IWorkItemWriter
    {
        public int TagWrites { get; private set; }
        public int CommentWrites { get; private set; }

        public Task<WorkItemMutationResult> UpdateIntakeTagsAsync(IntakeTagUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ExpectedRevision);
            TagWrites++;
            source.Tags.Clear();
            source.Tags.AddRange(request.FinalTags);
            source.Revision++;
            return Task.FromResult(WorkItemMutationResult.Success(source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), 200));
        }

        public Task<WorkItemMutationResult> AddValidatorCommentAsync(ValidatorCommentRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ExpectedRevision);
            Assert.Contains(request.Marker, request.Body, StringComparison.Ordinal);
            CommentWrites++;
            source.ValidatorComments.Add(request.Body);
            source.Revision++;
            return Task.FromResult(WorkItemMutationResult.Success(null, 200));
        }
    }

    private sealed class LiveFixture : IAsyncDisposable
    {
        private readonly string temporaryDirectory;
        private readonly string databasePath;
        private readonly string overridePath;
        private readonly string? previousOverride;
        private readonly WebApplicationFactory<Program> factory;

        private LiveFixture(string temporaryDirectory, string databasePath, string overridePath, string? previousOverride,
            WebApplicationFactory<Program> factory, MutableWriter writer)
        {
            this.temporaryDirectory = temporaryDirectory;
            this.databasePath = databasePath;
            this.overridePath = overridePath;
            this.previousOverride = previousOverride;
            this.factory = factory;
            Writer = writer;
            Client = factory.CreateClient();
        }

        public HttpClient Client { get; }
        public MutableWriter Writer { get; }

        public static async Task<LiveFixture> CreateAsync(MutableSource source, IIntakeAiProvider provider)
        {
            var root = FindRepositoryRoot();
            var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-phase12-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var sourceProfile = await File.ReadAllTextAsync(Path.Combine(root, "profiles", "example", "profile.yaml"));
            await File.WriteAllTextAsync(Path.Combine(directory, "profile.yaml"),
                sourceProfile.Replace("executionMode: DRY_RUN", "executionMode: LIVE", StringComparison.Ordinal));
            File.Copy(Path.Combine(root, "profiles", "example", "intake-policy.yaml"), Path.Combine(directory, "intake-policy.yaml"));
            var databasePath = Path.Combine(directory, "acceptance.db");
            var overridePath = Path.Combine(directory, "appsettings.json");
            await LegacyProfileTestSeeder.ImportAsync(databasePath, Path.Combine(directory, "profile.yaml"));
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var writer = new MutableWriter(source);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWorkItemSource>();
                    services.RemoveAll<IWorkItemWriter>();
                    services.RemoveAll<IIntakeAiProvider>();
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IWorkItemSource>(source);
                    services.AddSingleton<IWorkItemWriter>(writer);
                    services.AddSingleton(provider);
                    services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.Parse("2026-09-10T12:00:00Z")));
                });
            });
            var fixture = new LiveFixture(directory, databasePath, overridePath, previous, factory, writer);
            await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Client);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(overridePath)) File.Delete(overridePath);
            Directory.Delete(temporaryDirectory, recursive: true);
        }

        private sealed class FixedClock(DateTimeOffset utcNow) : IClock
        {
            public DateTimeOffset UtcNow { get; } = utcNow;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EngineeringIntakeGate.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate repository root.");
        }
    }
}

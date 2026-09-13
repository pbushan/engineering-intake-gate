using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.WorkItems;
using IntakeGate.Application.Time;
using IntakeGate.Host;
using IntakeGate.Host.Discovery;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class RuntimeGenerationIsolationTests
{
    [Fact]
    public async Task CFG_011_SchedulerStaysAliveProfilelessAndManualThenReactsToNewScheduleGeneration()
    {
        var configuration = new IntakeGate.Infrastructure.Configuration.YamlDeploymentConfigurationLoader().Load(
            Path.Combine(RepositoryRoot(), "profiles", "example", "profile.yaml"));
        var state = new DeploymentConfigurationState(null);
        var calculator = new RecordingScheduleCalculator();
        var captures = new RecordingCaptureFactory();
        var scheduler = new IncrementalRunHostedService(state, captures, calculator,
            new FixedClock(DateTimeOffset.Parse("2026-09-12T12:00:00Z")),
            NullLogger<IncrementalRunHostedService>.Instance);
        await scheduler.StartAsync(CancellationToken.None);

        state.Activate(Generation(1, configuration with
        {
            Profile = configuration.Profile with
            {
                Schedule = configuration.Profile.Schedule with { Enabled = false }
            }
        }));
        await Task.Delay(50);
        Assert.Equal(0, calculator.Calls);

        state.Activate(Generation(2, configuration with
        {
            Profile = configuration.Profile with
            {
                Schedule = configuration.Profile.Schedule with { Enabled = true }
            }
        }));
        await calculator.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await captures.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(calculator.Calls >= 1);
        Assert.Equal(1, captures.Calls);
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CFG_009_CFG_010_BlockedRunRetainsAdoAiSnapshotWhileNextRunUsesActivatedGeneration()
    {
        var root = RepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-runtime-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runtime.db");
        var settings = Path.Combine(directory, "appsettings.json");
        await LegacyProfileTestSeeder.ImportAsync(database,
            Path.Combine(root, "profiles", "example", "profile.yaml"));
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        {
            OperationalDatabase = new { Path = database }
        }));
        var prior = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
        Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", settings);
        var adoFactory = new RecordingAdoFactory();
        var aiFactory = new BlockingAiFactory();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRuntimeAzureDevOpsAdapterFactory>();
                services.RemoveAll<IRuntimeAiProviderFactory>();
                services.AddSingleton<IRuntimeAzureDevOpsAdapterFactory>(adoFactory);
                services.AddSingleton<IRuntimeAiProviderFactory>(aiFactory);
            });
        });
        try
        {
            using var client = factory.CreateClient();
            await AuthenticatedTestClient.AuthenticateAdminAsync(client);
            var generationRepository = factory.Services.GetRequiredService<IRuntimeConfigurationGenerationRepository>();
            var generationN = (await generationRepository.GetActiveAsync())!;

            var runA = client.PostAsync("/api/runs/work-items/42", null);
            await aiFactory.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secretStore = factory.Services.GetRequiredService<ISecretStore>();
            var replacementActor = new AuditActor(Guid.NewGuid(), "isolation-admin");
            await secretStore.ReplaceLocalAsync(CredentialSlot.AzureDevOps,
                new SecretValue("ado-local-generation-n1"), replacementActor);
            await secretStore.ReplaceLocalAsync(CredentialSlot.OpenAi,
                new SecretValue("ai-local-generation-n1"), replacementActor);
            var adoMetadataN1 = await secretStore.GetMetadataAsync(CredentialSlot.AzureDevOps);
            var aiMetadataN1 = await secretStore.GetMetadataAsync(CredentialSlot.OpenAi);

            var queryN1 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var configurationN1 = generationN.Configuration with
            {
                Profile = generationN.Configuration.Profile with
                {
                    Ado = generationN.Configuration.Profile.Ado with
                    {
                        OrganizationUrl = new Uri("https://dev.azure.com/generation-n1"),
                        Project = "GenerationN1",
                        SavedQueryId = queryN1
                    },
                    Ai = generationN.Configuration.Profile.Ai with { Model = "model-N1" }
                },
                RuntimeGenerationId = null
            };
            await UpdateAuthoritativeProfileAsync(database, configurationN1);
            var fingerprintN1 = RuntimeConfigurationFingerprint.Create(configurationN1,
                adoMetadataN1.UpdatedAtUtc!.Value, aiMetadataN1.UpdatedAtUtc!.Value);
            var generationN1 = await generationRepository.ActivateAsync(
                configurationN1, fingerprintN1,
                new RuntimeCredentialBinding(CredentialSlot.AzureDevOps,
                    SecretSourceKind.LocallyEncrypted, adoMetadataN1.UpdatedAtUtc!.Value),
                new RuntimeCredentialBinding(CredentialSlot.OpenAi,
                    SecretSourceKind.LocallyEncrypted, aiMetadataN1.UpdatedAtUtc!.Value),
                replacementActor,
                DateTimeOffset.UtcNow, ["ado", "ai.model"]);
            factory.Services.GetRequiredService<DeploymentConfigurationState>().Activate(generationN1);

            aiFactory.ReleaseFirstRequest.TrySetResult();
            using var responseA = await runA;
            responseA.EnsureSuccessStatusCode();
            using var responseB = await client.PostAsync("/api/runs/work-items/42", null);
            responseB.EnsureSuccessStatusCode();

            Assert.Equal(["example-model", "model-N1"], aiFactory.ObservedModels.ToArray());
            Assert.Equal(["acceptance-ai-runtime-credential", "ai-local-generation-n1"],
                aiFactory.ObservedCredentials.ToArray());
            Assert.Equal(generationN.Configuration.Profile.Ado.SavedQueryId, adoFactory.Observed.Single(x => x.Model == "example-model").Query);
            Assert.Equal(queryN1, adoFactory.Observed.Single(x => x.Model == "model-N1").Query);
            Assert.Equal("acceptance-ado-runtime-credential",
                adoFactory.Observed.Single(x => x.Model == "example-model").Credential);
            Assert.Equal("ado-local-generation-n1",
                adoFactory.Observed.Single(x => x.Model == "model-N1").Credential);
            var runAId = (await responseA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
            var runBId = (await responseB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
            Assert.Equal(generationN.GenerationId, await RunGenerationAsync(database, runAId));
            Assert.Equal(generationN1.GenerationId, await RunGenerationAsync(database, runBId));
            var persistedGenerations = await File.ReadAllTextAsync(database);
            Assert.DoesNotContain("ado-local-generation-n1", persistedGenerations, StringComparison.Ordinal);
            Assert.DoesNotContain("ai-local-generation-n1", persistedGenerations, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", prior);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingAdoFactory : IRuntimeAzureDevOpsAdapterFactory
    {
        public ConcurrentBag<(string Model, Guid Query, string Credential)> Observed { get; } = [];

        public RuntimeAzureDevOpsAdapters Create(DeploymentConfiguration configuration, SecretValue credential)
        {
            Observed.Add((configuration.Profile.Ai.Model, configuration.Profile.Ado.SavedQueryId,
                credential.DangerousGetValue()));
            return new RuntimeAzureDevOpsAdapters(new Source(), new Writer());
        }

        private sealed class Source : IWorkItemSource
        {
            public Task<WorkItemQueryResult> ExecuteSavedQueryAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new WorkItemQueryResult([42]));
            public Task<WorkItemReadResult> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new WorkItemReadResult(new RawWorkItem("42", "1", "Generic", "Generation isolation")));
        }

        private sealed class Writer : IWorkItemWriter
        {
            public Task<WorkItemMutationResult> UpdateIntakeTagsAsync(IntakeTagUpdateRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(WorkItemMutationResult.Success("2"));
            public Task<WorkItemMutationResult> AddValidatorCommentAsync(ValidatorCommentRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(WorkItemMutationResult.Success(null));
        }
    }

    private sealed class BlockingAiFactory : IRuntimeAiProviderFactory
    {
        private int calls;
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> ObservedModels { get; } = [];
        public ConcurrentQueue<string> ObservedCredentials { get; } = [];

        public IIntakeAiProvider Create(DeploymentConfiguration configuration, SecretValue credential)
        {
            ObservedCredentials.Enqueue(credential.DangerousGetValue());
            return new Provider(this);
        }

        private sealed class Provider(BlockingAiFactory owner) : IIntakeAiProvider
        {
            public async Task<AiProviderResponse> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken)
            {
                owner.ObservedModels.Enqueue(request.ModelIdentifier);
                if (Interlocked.Increment(ref owner.calls) == 1)
                {
                    owner.FirstRequestStarted.TrySetResult();
                    await owner.ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
                return await FakeIntakeAiProvider.ReusablePass().EvaluateAsync(request, cancellationToken);
            }
        }
    }

    private sealed class RecordingScheduleCalculator : IIncrementalScheduleCalculator
    {
        public int Calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset GetNextOccurrenceUtc(ScheduleConfiguration schedule, DateTimeOffset nowUtc)
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            return nowUtc + TimeSpan.FromMilliseconds(20);
        }
    }

    private sealed class RecordingCaptureFactory : IRuntimeExecutionFactory
    {
        public int Calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RuntimeExecutionCapture> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            return Task.FromResult(new RuntimeExecutionCapture(null, RuntimeExecutionCaptureFailure.NoActiveGeneration));
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private static RuntimeConfigurationGeneration Generation(long id, DeploymentConfiguration configuration) =>
        new(id, configuration.Profile.Identity.Id, configuration with { RuntimeGenerationId = id }, $"generation-{id}",
            new RuntimeCredentialBinding(CredentialSlot.AzureDevOps, SecretSourceKind.EnvironmentReference,
                DateTimeOffset.Parse("2026-09-12T12:00:00Z")),
            new RuntimeCredentialBinding(CredentialSlot.OpenAi, SecretSourceKind.EnvironmentReference,
                DateTimeOffset.Parse("2026-09-12T12:00:00Z")),
            DateTimeOffset.Parse("2026-09-12T12:00:00Z"));

    private static async Task<long> RunGenerationAsync(string database, Guid runId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_generation_id FROM run_audits WHERE run_id = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task UpdateAuthoritativeProfileAsync(string database, DeploymentConfiguration configuration)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE singleton_profile_configuration SET profile_json = $profile WHERE singleton_id = 1;";
        command.Parameters.AddWithValue("$profile", JsonSerializer.Serialize(
            DeploymentConfigurationInputs.Profile(configuration.Profile), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await command.ExecuteNonQueryAsync();
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EngineeringIntakeGate.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}

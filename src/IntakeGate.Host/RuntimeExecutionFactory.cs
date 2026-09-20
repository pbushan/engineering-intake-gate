using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Decision;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;
using IntakeGate.Application.WorkItems;
using IntakeGate.Infrastructure.Ai;
using IntakeGate.Infrastructure.AzureDevOps;
using IntakeGate.Infrastructure.Evidence;

namespace IntakeGate.Host;

public enum RuntimeExecutionCaptureFailure { NoActiveGeneration, CredentialRevisionUnavailable }

public sealed record RuntimeExecutionCapture(RuntimeExecutionServices? Services, RuntimeExecutionCaptureFailure? Failure)
{
    public bool Succeeded => Services is not null && Failure is null;
}

public sealed record RuntimeExecutionServices(
    RuntimeConfigurationGeneration Generation,
    IncrementalRunService IncrementalRuns,
    ManualWorkItemRunService ManualRuns,
    IIntakeAiProvider AiProvider);

public interface IRuntimeExecutionFactory
{
    Task<RuntimeExecutionCapture> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds all provider-bearing execution services once from one captured generation.</summary>
public sealed class RuntimeExecutionFactory(
    IServiceProvider services,
    IRuntimeConfigurationGenerationRepository generations,
    ISecretStore secrets,
    IRuntimeAzureDevOpsAdapterFactory azureDevOpsFactory,
    IRuntimeAiProviderFactory aiFactory) : IRuntimeExecutionFactory
{
    public async Task<RuntimeExecutionCapture> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var generation = await generations.GetActiveAsync(cancellationToken);
        if (generation is null) return new(null, RuntimeExecutionCaptureFailure.NoActiveGeneration);

        var adoSecret = await ResolveExactAsync(generation.AzureDevOpsCredential, cancellationToken);
        var aiSecret = await ResolveExactAsync(generation.AiCredential, cancellationToken);
        if (adoSecret is null || aiSecret is null)
            return new(null, RuntimeExecutionCaptureFailure.CredentialRevisionUnavailable);

        var configuration = generation.Configuration;
        var ado = azureDevOpsFactory.Create(configuration, adoSecret);
        var provider = aiFactory.Create(configuration, aiSecret);
        var redactor = services.GetRequiredService<ISecretRedactor>();
        var attachmentProvider = new ConfiguredAttachmentEvidenceAiProvider(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, configuration,
            new FixedProviderCredentialResolver(aiSecret.DangerousGetValue()));
        var attachmentProcessing = new AttachmentProcessingService(
            [
                new TextAttachmentProcessor(),
                new PdfAttachmentProcessor(services.GetRequiredService<IPdfPageRenderer>(), attachmentProvider, redactor),
                new ImageAttachmentProcessor(),
                new SpreadsheetAttachmentProcessor(),
                new DocxAttachmentProcessor(),
                new MediaAttachmentProcessor(services.GetRequiredService<IMediaTool>(), attachmentProvider, redactor)
            ],
            services.GetRequiredService<IAnalysisCacheRepository>(), redactor);
        var evidencePreprocessor = new EvidencePreprocessor(
            services.GetRequiredService<IContentNormalizer>(), redactor,
            services.GetRequiredService<IEvidenceProcessingLog>(), attachmentProcessing);
        var evaluation = new IntakeEvaluationService(
            services.GetRequiredService<IEvaluationRequestBuilder>(), provider,
            services.GetRequiredService<EvaluationResponseParser>(),
            services.GetRequiredService<EvaluationContractValidator>(),
            services.GetRequiredService<IEvaluationLog>());
        var intake = new IntakeRunService(
            evidencePreprocessor, evaluation,
            services.GetRequiredService<IIntakeDecisionHandler>(),
            services.GetRequiredService<IAuditRepository>(), services.GetRequiredService<IClock>(),
            services.GetRequiredService<IRunAuditLog>(), services.GetRequiredService<IAiCostAccountingService>(),
            ado.Source, ado.Writer, services.GetRequiredService<IAnalysisCacheRepository>());
        var reconciliation = new MutationReconciliationService(
            ado.Source, ado.Writer, services.GetRequiredService<IReconciliationRepository>(),
            services.GetRequiredService<IAuditRepository>(), services.GetRequiredService<IClock>(),
            services.GetRequiredService<IRetryDelay>());
        var manual = new ManualWorkItemRunService(
            ado.Source, services.GetRequiredService<IWorkItemEligibilityEvaluator>(), intake,
            services.GetRequiredService<IAuditRepository>(), services.GetRequiredService<IClock>(),
            services.GetRequiredService<IWorkItemReadLog>());
        var incremental = new IncrementalRunService(
            ado.Source, services.GetRequiredService<IWorkItemEligibilityEvaluator>(), intake,
            services.GetRequiredService<IAuditRepository>(), services.GetRequiredService<IIncrementalDiscoveryRepository>(),
            services.GetRequiredService<IClock>(), services.GetRequiredService<IWorkItemReadLog>(), reconciliation);
        return new(new RuntimeExecutionServices(generation, incremental, manual, provider), null);
    }

    private sealed class FixedProviderCredentialResolver(string value) : IProviderCredentialResolver
    {
        public string? Resolve(string environmentVariableName) => value;
    }

    private async Task<SecretValue?> ResolveExactAsync(RuntimeCredentialBinding binding, CancellationToken cancellationToken)
    {
        var before = await secrets.GetMetadataAsync(binding.Slot, cancellationToken);
        if (!Matches(before, binding)) return null;
        var resolution = await secrets.ResolveAsync(binding.Slot, cancellationToken);
        if (resolution.Availability != SecretAvailability.Available || resolution.Secret is null) return null;
        var after = await secrets.GetMetadataAsync(binding.Slot, cancellationToken);
        return Matches(after, binding) ? resolution.Secret : null;
    }

    private static bool Matches(CredentialMetadata metadata, RuntimeCredentialBinding binding) =>
        metadata.Configured && metadata.SourceKind == binding.SourceKind && metadata.UpdatedAtUtc == binding.Revision;
}

public sealed class AzureDevOpsRuntimeAdapterFactory(IWorkItemReadLog log, IConfiguration hostConfiguration)
    : IRuntimeAzureDevOpsAdapterFactory
{
    public RuntimeAzureDevOpsAdapters Create(DeploymentConfiguration snapshot, SecretValue credential)
    {
        var resolver = new FixedAzureDevOpsCredentialResolver(credential.DangerousGetValue());
        return new RuntimeAzureDevOpsAdapters(
            new AzureDevOpsWorkItemSource(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, snapshot.Profile.Ado,
                resolver, log, snapshot.Profile.Processing.Retries,
                snapshot.Profile.Processing.AttachmentLimits.MaximumBytesPerAttachment,
                enableAttachmentDownloads: hostConfiguration.GetValue("AdoRuntime:ReadAttachments", true)),
            new AzureDevOpsWorkItemWriter(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, snapshot.Profile.Ado, resolver));
    }

    private sealed class FixedAzureDevOpsCredentialResolver(string value) : IAzureDevOpsCredentialResolver
    {
        public string? Resolve(string environmentVariableName) => value;
    }
}

public sealed class AiRuntimeProviderFactory : IRuntimeAiProviderFactory
{
    public IIntakeAiProvider Create(DeploymentConfiguration snapshot, SecretValue credential)
    {
        var resolver = new FixedProviderCredentialResolver(credential.DangerousGetValue());
        var timeout = TimeSpan.FromSeconds(snapshot.Profile.Ai.TimeoutSeconds);
        return snapshot.Profile.Ai.Provider switch
        {
            "openai" => new OpenAiIntakeAiProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                resolver, snapshot.Profile.Ai.Authentication.EnvironmentVariable, timeout),
            "anthropic" => new AnthropicIntakeAiProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                resolver, snapshot.Profile.Ai.Authentication.EnvironmentVariable, timeout),
            _ => throw new InvalidOperationException("The active runtime generation has an unsupported AI provider.")
        };
    }

    private sealed class FixedProviderCredentialResolver(string value) : IProviderCredentialResolver
    {
        public string? Resolve(string environmentVariableName) => value;
    }
}

public sealed class RegisteredRuntimeAzureDevOpsAdapterFactory(IServiceProvider services)
    : IRuntimeAzureDevOpsAdapterFactory
{
    public RuntimeAzureDevOpsAdapters Create(DeploymentConfiguration configuration, SecretValue credential) =>
        new(services.GetRequiredService<IWorkItemSource>(), services.GetRequiredService<IWorkItemWriter>());
}

public sealed class RegisteredRuntimeAiProviderFactory(IServiceProvider services) : IRuntimeAiProviderFactory
{
    public IIntakeAiProvider Create(DeploymentConfiguration configuration, SecretValue credential) =>
        services.GetRequiredService<IIntakeAiProvider>();
}

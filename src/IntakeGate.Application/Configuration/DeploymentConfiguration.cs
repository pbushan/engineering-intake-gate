namespace IntakeGate.Application.Configuration;

public static class DeploymentConfigurationDefaults
{
    public const int AiTimeoutSeconds = 60;
    public const int MaximumImageCount = 10;
    public const long MaximumImageBytes = 5_242_880;
    public const int MaximumCsvRows = 1_000;
    public const int MaximumStructuredTextDepth = 32;
}

public sealed record DeploymentConfiguration(DeploymentProfile Profile, IntakePolicy Policy, string PolicyFingerprint)
{
    /// <summary>Set only when this value was reconstructed from an immutable runtime generation.</summary>
    public long? RuntimeGenerationId { get; init; }
}

public sealed record DeploymentProfile(
    ProfileIdentity Identity,
    IntakePolicyReference IntakePolicy,
    IntakeStateConfiguration IntakeState,
    AzureDevOpsConfiguration Ado,
    AiConfiguration Ai,
    ScheduleConfiguration Schedule,
    ProcessingConfiguration Processing,
    AuditConfiguration Audit,
    IReadOnlyList<ExclusionRule> Exclusions);

public sealed record ProfileIdentity(string Id, string? Version);

public sealed record IntakePolicyReference(string Path, Uri Url);

public sealed record AzureDevOpsConfiguration(
    Uri OrganizationUrl,
    string Project,
    Guid SavedQueryId,
    CredentialReference Authentication);

public sealed record AiConfiguration(string Provider, string Model, CredentialReference Authentication)
{
    public int TimeoutSeconds { get; init; } = DeploymentConfigurationDefaults.AiTimeoutSeconds;
    public IReadOnlyList<ModelPricing> Pricing { get; init; } = [];
}

public sealed record ModelPricing(
    string Provider,
    string Model,
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens,
    string Currency,
    string? Identity);

public sealed record CredentialReference(string EnvironmentVariable);

public sealed record ScheduleConfiguration(bool Enabled, string Expression, string Timezone, TimeSpan InitialLookback);

public sealed record IntakeStateConfiguration(string ValidatedTag, string IncompleteTag);

public enum ExecutionMode
{
    Live,
    DryRun
}

public sealed record ProcessingConfiguration(
    ExecutionMode ExecutionMode,
    int Concurrency,
    int Retries,
    ContentLimits ContentLimits,
    AttachmentLimits AttachmentLimits)
{
    public bool DryRun => ExecutionMode == ExecutionMode.DryRun;
}

public sealed record ContentLimits(int MaximumTotalCharacters, int MaximumComments, int MaximumExtractedTextCharacters);

public sealed record AttachmentLimits(
    int MaximumCount,
    long MaximumBytesPerAttachment,
    long MaximumAggregateBytes,
    int MaximumPdfPages,
    int MaximumImageCount = DeploymentConfigurationDefaults.MaximumImageCount,
    long MaximumImageBytes = DeploymentConfigurationDefaults.MaximumImageBytes,
    int MaximumCsvRows = DeploymentConfigurationDefaults.MaximumCsvRows,
    int MaximumStructuredTextDepth = DeploymentConfigurationDefaults.MaximumStructuredTextDepth);

public sealed record AuditConfiguration(int RetentionDays);

public sealed record ExclusionRule(string Id, string Field, ExclusionOperator Operator, IReadOnlyList<string> Values);

public enum ExclusionOperator
{
    EqualsAny
}

public sealed record IntakePolicy(PolicyIdentity Identity, IReadOnlyList<IntakeCriterion> Criteria);

public sealed record PolicyIdentity(string Id, string Version);

public sealed record IntakeCriterion(
    string Id,
    string DisplayName,
    string Description,
    CriterionApplicability Applicability,
    NotApplicablePolicy NotApplicable,
    string EvaluationGuidance);

public enum CriterionApplicability
{
    Required,
    Contextual
}

public sealed record NotApplicablePolicy(bool Allowed, bool RequiresExplanation);

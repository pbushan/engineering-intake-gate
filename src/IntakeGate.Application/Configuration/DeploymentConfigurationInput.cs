namespace IntakeGate.Application.Configuration;

/// <summary>
/// Provider-neutral, unvalidated configuration input. YAML, SQLite, import, and API adapters can
/// populate these shapes and delegate all semantic rules to <see cref="DeploymentConfigurationValidator"/>.
/// </summary>
public sealed class DeploymentProfileInput
{
    public ConfigurationIdentityInput? Profile { get; init; }
    public PolicyReferenceInput? IntakePolicy { get; init; }
    public IntakeStateInput? IntakeState { get; init; }
    public AzureDevOpsInput? Ado { get; init; }
    public AiInput? Ai { get; init; }
    public ScheduleInput? Schedule { get; init; }
    public ProcessingInput? Processing { get; init; }
    public AuditInput? Audit { get; init; }
    public List<ExclusionInput>? Exclusions { get; init; }
}

public sealed class IntakePolicyInput
{
    public ConfigurationIdentityInput? Policy { get; init; }
    public List<CriterionInput>? Criteria { get; init; }
}

public sealed class ConfigurationIdentityInput { public string? Id { get; init; } public string? Version { get; init; } }
public sealed class PolicyReferenceInput { public string? Path { get; init; } public string? Url { get; init; } }
public sealed class IntakeStateInput { public string? ValidatedTag { get; init; } public string? IncompleteTag { get; init; } }
public sealed class AzureDevOpsInput { public string? OrganizationUrl { get; init; } public string? Project { get; init; } public string? SavedQueryId { get; init; } public AzureDevOpsAuthenticationInput? Authentication { get; init; } }
public sealed class AzureDevOpsAuthenticationInput { public string? PatEnvironmentVariable { get; init; } }
public sealed class AiInput { public string? Provider { get; init; } public string? Model { get; init; } public int? TimeoutSeconds { get; init; } public AiAuthenticationInput? Authentication { get; init; } public List<ModelPricingInput>? Pricing { get; init; } }
public sealed class AiAuthenticationInput { public string? ApiKeyEnvironmentVariable { get; init; } }
public sealed class ModelPricingInput { public string? Provider { get; init; } public string? Model { get; init; } public decimal? InputPerMillionTokens { get; init; } public decimal? OutputPerMillionTokens { get; init; } public string? Currency { get; init; } public string? Identity { get; init; } }
public sealed class ScheduleInput { public bool Enabled { get; init; } public string? Expression { get; init; } public string? Timezone { get; init; } public string? InitialLookback { get; init; } }
public sealed class ProcessingInput { public string? ExecutionMode { get; init; } public int? Concurrency { get; init; } public int? Retries { get; init; } public ContentLimitsInput? ContentLimits { get; init; } public AttachmentLimitsInput? AttachmentLimits { get; init; } }
public sealed class ContentLimitsInput { public int? MaximumTotalCharacters { get; init; } public int? MaximumComments { get; init; } public int? MaximumExtractedTextCharacters { get; init; } }
public sealed class AttachmentLimitsInput { public int? MaximumCount { get; init; } public long? MaximumBytesPerAttachment { get; init; } public long? MaximumAggregateBytes { get; init; } public int? MaximumPdfPages { get; init; } public int? MaximumImageCount { get; init; } public long? MaximumImageBytes { get; init; } public int? MaximumCsvRows { get; init; } public int? MaximumStructuredTextDepth { get; init; } }
public sealed class AuditInput
{
    public int? RetentionDays { get; init; }
    public int? EvidenceRetentionDays { get; init; }
    public int? MaximumSelectedVideoScreenshots { get; init; }
}
public sealed class ExclusionInput { public string? Id { get; init; } public string? Field { get; init; } public string? Operator { get; init; } public List<string>? Values { get; init; } }
public sealed class CriterionInput { public string? Id { get; init; } public string? DisplayName { get; init; } public string? Description { get; init; } public string? Applicability { get; init; } public NotApplicableInput? Na { get; init; } public string? EvaluationGuidance { get; init; } }
public sealed class NotApplicableInput { public bool? Allowed { get; init; } public bool? RequiresExplanation { get; init; } }

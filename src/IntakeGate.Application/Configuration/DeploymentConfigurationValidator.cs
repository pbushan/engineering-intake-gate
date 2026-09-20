using System.Globalization;
using System.Text.RegularExpressions;
using IntakeGate.Application.AzureDevOps;

namespace IntakeGate.Application.Configuration;

public interface IDeploymentConfigurationValidator
{
    DeploymentConfiguration Validate(
        DeploymentProfileInput profileInput,
        IntakePolicyInput policyInput,
        string profileSource = "profile",
        string policySource = "intake policy");

    DeploymentProfile ValidateProfile(DeploymentProfileInput input, string source = "profile");

    IntakePolicy ValidatePolicy(IntakePolicyInput input, string source = "intake policy");

    DeploymentConfiguration ValidateConfiguration(
        DeploymentConfiguration configuration,
        string profileSource = "deployment profile",
        string policySource = "intake policy") => Validate(
            DeploymentConfigurationInputs.Profile(configuration.Profile),
            DeploymentConfigurationInputs.Policy(configuration.Policy),
            profileSource,
            policySource);
}

/// <summary>Single semantic validation and normalization path for profile and policy inputs.</summary>
public sealed class DeploymentConfigurationValidator : IDeploymentConfigurationValidator
{
    private static readonly Regex EnvironmentVariableName = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex KnownCredentialPrefix = new(
        "^(?:sk-|AIza|gh[pousr]_)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    public DeploymentConfiguration Validate(
        DeploymentProfileInput profileInput,
        IntakePolicyInput policyInput,
        string profileSource = "profile",
        string policySource = "intake policy")
    {
        var profile = ValidateProfile(profileInput, profileSource);
        var policy = ValidatePolicy(policyInput, policySource);
        return new DeploymentConfiguration(profile, policy, PolicyFingerprint.Create(policy));
    }

    public DeploymentConfiguration ValidateConfiguration(
        DeploymentConfiguration configuration,
        string profileSource = "deployment profile",
        string policySource = "intake policy")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Validate(
            DeploymentConfigurationInputs.Profile(configuration.Profile),
            DeploymentConfigurationInputs.Policy(configuration.Policy),
            profileSource,
            policySource);
    }

    public DeploymentProfile ValidateProfile(DeploymentProfileInput input, string source = "profile")
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<string>();
        Require(input.Profile?.Id, "profile.id", errors);
        Require(input.IntakePolicy?.Path, "intakePolicy.path", errors);
        var policyUrl = ParseHttpUri(input.IntakePolicy?.Url, "intakePolicy.url", errors);
        Require(input.IntakeState?.ValidatedTag, "intakeState.validatedTag", errors);
        Require(input.IntakeState?.IncompleteTag, "intakeState.incompleteTag", errors);
        if (!string.IsNullOrWhiteSpace(input.IntakeState?.ValidatedTag) &&
            string.Equals(input.IntakeState.ValidatedTag.Trim(), input.IntakeState.IncompleteTag?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("intakeState.validatedTag and intakeState.incompleteTag must be different.");
        }

        var organizationUrl = ParseAzureDevOpsOrganizationUri(input.Ado?.OrganizationUrl, "ado.organizationUrl", errors);
        Require(input.Ado?.Project, "ado.project", errors);
        var savedQueryId = ParseGuid(input.Ado?.SavedQueryId, "ado.savedQueryId", errors);
        Require(input.Ado?.Authentication?.PatEnvironmentVariable, "ado.authentication.patEnvironmentVariable", errors);
        EnvironmentVariableReference(input.Ado?.Authentication?.PatEnvironmentVariable,
            "ado.authentication.patEnvironmentVariable", errors);
        Require(input.Ai?.Provider, "ai.provider", errors);
        if (!string.IsNullOrWhiteSpace(input.Ai?.Provider) &&
            !string.Equals(input.Ai.Provider, "openai", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Ai.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ai.provider must be one of: openai, anthropic.");
        }

        Require(input.Ai?.Model, "ai.model", errors);
        Require(input.Ai?.Authentication?.ApiKeyEnvironmentVariable, "ai.authentication.apiKeyEnvironmentVariable", errors);
        EnvironmentVariableReference(input.Ai?.Authentication?.ApiKeyEnvironmentVariable,
            "ai.authentication.apiKeyEnvironmentVariable", errors);
        Positive(input.Ai?.TimeoutSeconds, "ai.timeoutSeconds", errors);
        var pricing = new List<ModelPricing>();
        foreach (var price in input.Ai?.Pricing ?? [])
        {
            Require(price.Provider, "ai.pricing[].provider", errors);
            Require(price.Model, "ai.pricing[].model", errors);
            Require(price.Currency, "ai.pricing[].currency", errors);
            NonNegative(price.InputPerMillionTokens, "ai.pricing[].inputPerMillionTokens", errors);
            NonNegative(price.OutputPerMillionTokens, "ai.pricing[].outputPerMillionTokens", errors);
            pricing.Add(new ModelPricing(
                price.Provider?.Trim().ToLowerInvariant() ?? string.Empty,
                price.Model?.Trim() ?? string.Empty,
                price.InputPerMillionTokens ?? 0,
                price.OutputPerMillionTokens ?? 0,
                price.Currency?.Trim().ToUpperInvariant() ?? string.Empty,
                NullIfBlank(price.Identity)));
        }

        if (pricing.GroupBy(item => $"{item.Provider}\0{item.Model}", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            errors.Add("ai.pricing must not contain duplicate provider/model entries.");
        Require(input.Schedule?.Timezone, "schedule.timezone", errors);
        if (input.Schedule?.Enabled == true) Require(input.Schedule.Expression, "schedule.expression", errors);

        var initialLookback = ParsePositiveTimeSpan(input.Schedule?.InitialLookback, "schedule.initialLookback", errors);
        var executionMode = input.Processing?.ExecutionMode?.Trim().ToUpperInvariant() switch
        {
            "DRY_RUN" => ExecutionMode.DryRun,
            "LIVE" => ExecutionMode.Live,
            _ => (ExecutionMode?)null
        };
        if (executionMode is null) errors.Add("processing.executionMode must be DRY_RUN or LIVE.");
        Positive(input.Processing?.Concurrency, "processing.concurrency", errors);
        NonNegative(input.Processing?.Retries, "processing.retries", errors);
        Positive(input.Processing?.ContentLimits?.MaximumTotalCharacters, "processing.contentLimits.maximumTotalCharacters", errors);
        Positive(input.Processing?.ContentLimits?.MaximumComments, "processing.contentLimits.maximumComments", errors);
        Positive(input.Processing?.ContentLimits?.MaximumExtractedTextCharacters, "processing.contentLimits.maximumExtractedTextCharacters", errors);
        Positive(input.Processing?.AttachmentLimits?.MaximumCount, "processing.attachmentLimits.maximumCount", errors);
        Positive(input.Processing?.AttachmentLimits?.MaximumBytesPerAttachment, "processing.attachmentLimits.maximumBytesPerAttachment", errors);
        Positive(input.Processing?.AttachmentLimits?.MaximumAggregateBytes, "processing.attachmentLimits.maximumAggregateBytes", errors);
        Positive(input.Processing?.AttachmentLimits?.MaximumPdfPages, "processing.attachmentLimits.maximumPdfPages", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumImageCount, "processing.attachmentLimits.maximumImageCount", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumImageBytes, "processing.attachmentLimits.maximumImageBytes", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumCsvRows, "processing.attachmentLimits.maximumCsvRows", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumStructuredTextDepth, "processing.attachmentLimits.maximumStructuredTextDepth", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSpreadsheetSheets, "processing.attachmentLimits.maximumSpreadsheetSheets", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSpreadsheetRowsPerSheet, "processing.attachmentLimits.maximumSpreadsheetRowsPerSheet", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSpreadsheetColumns, "processing.attachmentLimits.maximumSpreadsheetColumns", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSpreadsheetCells, "processing.attachmentLimits.maximumSpreadsheetCells", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumMediaDurationSeconds, "processing.attachmentLimits.maximumMediaDurationSeconds", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumMediaDimension, "processing.attachmentLimits.maximumMediaDimension", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumDecodedPixels, "processing.attachmentLimits.maximumDecodedPixels", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSampledFrames, "processing.attachmentLimits.maximumSampledFrames", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumFrameBytes, "processing.attachmentLimits.maximumFrameBytes", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumSelectedVideoScreenshots, "processing.attachmentLimits.maximumSelectedVideoScreenshots", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumRetainedScreenshotBytes, "processing.attachmentLimits.maximumRetainedScreenshotBytes", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumTranscriptCharacters, "processing.attachmentLimits.maximumTranscriptCharacters", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MediaProcessTimeoutSeconds, "processing.attachmentLimits.mediaProcessTimeoutSeconds", errors);
        PositiveIfSpecified(input.Processing?.AttachmentLimits?.MaximumConcurrentMediaJobs, "processing.attachmentLimits.maximumConcurrentMediaJobs", errors);
        AtMostInt32(input.Processing?.AttachmentLimits?.MaximumBytesPerAttachment, "processing.attachmentLimits.maximumBytesPerAttachment", errors);
        AtMostInt32(input.Processing?.AttachmentLimits?.MaximumImageBytes, "processing.attachmentLimits.maximumImageBytes", errors);
        AtMostInt32(input.Processing?.AttachmentLimits?.MaximumFrameBytes, "processing.attachmentLimits.maximumFrameBytes", errors);
        AtMostInt32(input.Processing?.AttachmentLimits?.MaximumRetainedScreenshotBytes, "processing.attachmentLimits.maximumRetainedScreenshotBytes", errors);
        if (input.Processing?.AttachmentLimits?.MaximumSelectedVideoScreenshots is > DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots)
            errors.Add($"processing.attachmentLimits.maximumSelectedVideoScreenshots must be {DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots} or less.");
        Positive(input.Audit?.RetentionDays, "audit.retentionDays", errors);
        PositiveIfSpecified(input.Audit?.EvidenceRetentionDays, "audit.evidenceRetentionDays", errors);
        PositiveIfSpecified(input.Audit?.MaximumSelectedVideoScreenshots, "audit.maximumSelectedVideoScreenshots", errors);
        if (input.Audit?.EvidenceRetentionDays is > 3650)
            errors.Add("audit.evidenceRetentionDays must be 3650 or less.");
        if (input.Audit?.MaximumSelectedVideoScreenshots is > DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots)
            errors.Add($"audit.maximumSelectedVideoScreenshots must be {DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots} or less.");

        var exclusions = new List<ExclusionRule>();
        foreach (var exclusion in input.Exclusions ?? [])
        {
            Require(exclusion.Id, "exclusions[].id", errors);
            Require(exclusion.Field, "exclusions[].field", errors);
            if (!string.Equals(exclusion.Operator, "equalsAny", StringComparison.OrdinalIgnoreCase))
                errors.Add("exclusions[].operator must be equalsAny.");
            if (exclusion.Values is null || exclusion.Values.Count == 0 || exclusion.Values.Any(string.IsNullOrWhiteSpace))
                errors.Add("exclusions[].values must contain at least one non-blank value.");
            exclusions.Add(new ExclusionRule(exclusion.Id ?? string.Empty, exclusion.Field ?? string.Empty,
                ExclusionOperator.EqualsAny, exclusion.Values ?? []));
        }

        ThrowIfInvalid(source, errors);
        return new DeploymentProfile(
            new ProfileIdentity(input.Profile!.Id!.Trim(), NullIfBlank(input.Profile.Version)),
            new IntakePolicyReference(input.IntakePolicy!.Path!.Trim(), policyUrl!),
            new IntakeStateConfiguration(input.IntakeState!.ValidatedTag!.Trim(), input.IntakeState.IncompleteTag!.Trim()),
            new AzureDevOpsConfiguration(organizationUrl!, input.Ado!.Project!.Trim(), savedQueryId,
                new CredentialReference(input.Ado.Authentication!.PatEnvironmentVariable!.Trim())),
            new AiConfiguration(input.Ai!.Provider!.Trim().ToLowerInvariant(), input.Ai.Model!.Trim(),
                new CredentialReference(input.Ai.Authentication!.ApiKeyEnvironmentVariable!.Trim()))
            {
                TimeoutSeconds = input.Ai.TimeoutSeconds!.Value,
                Pricing = pricing
            },
            new ScheduleConfiguration(input.Schedule?.Enabled ?? false, input.Schedule?.Expression?.Trim() ?? string.Empty,
                input.Schedule!.Timezone!.Trim(), initialLookback),
            new ProcessingConfiguration(
                executionMode!.Value,
                input.Processing!.Concurrency!.Value,
                input.Processing.Retries!.Value,
                new ContentLimits(input.Processing.ContentLimits!.MaximumTotalCharacters!.Value,
                    input.Processing.ContentLimits.MaximumComments!.Value,
                    input.Processing.ContentLimits.MaximumExtractedTextCharacters!.Value),
                new AttachmentLimits(
                    input.Processing.AttachmentLimits!.MaximumCount!.Value,
                    input.Processing.AttachmentLimits.MaximumBytesPerAttachment!.Value,
                    input.Processing.AttachmentLimits.MaximumAggregateBytes!.Value,
                    input.Processing.AttachmentLimits.MaximumPdfPages!.Value,
                    input.Processing.AttachmentLimits.MaximumImageCount ?? input.Processing.AttachmentLimits.MaximumCount.Value,
                    input.Processing.AttachmentLimits.MaximumImageBytes ?? input.Processing.AttachmentLimits.MaximumBytesPerAttachment.Value,
                    input.Processing.AttachmentLimits.MaximumCsvRows ?? 1_000,
                    input.Processing.AttachmentLimits.MaximumStructuredTextDepth ?? 32)
                {
                    MaximumSpreadsheetSheets = input.Processing.AttachmentLimits.MaximumSpreadsheetSheets ?? DeploymentConfigurationDefaults.MaximumSpreadsheetSheets,
                    MaximumSpreadsheetRowsPerSheet = input.Processing.AttachmentLimits.MaximumSpreadsheetRowsPerSheet ?? DeploymentConfigurationDefaults.MaximumSpreadsheetRowsPerSheet,
                    MaximumSpreadsheetColumns = input.Processing.AttachmentLimits.MaximumSpreadsheetColumns ?? DeploymentConfigurationDefaults.MaximumSpreadsheetColumns,
                    MaximumSpreadsheetCells = input.Processing.AttachmentLimits.MaximumSpreadsheetCells ?? DeploymentConfigurationDefaults.MaximumSpreadsheetCells,
                    MaximumMediaDurationSeconds = input.Processing.AttachmentLimits.MaximumMediaDurationSeconds ?? DeploymentConfigurationDefaults.MaximumMediaDurationSeconds,
                    MaximumMediaDimension = input.Processing.AttachmentLimits.MaximumMediaDimension ?? DeploymentConfigurationDefaults.MaximumMediaDimension,
                    MaximumDecodedPixels = input.Processing.AttachmentLimits.MaximumDecodedPixels ?? DeploymentConfigurationDefaults.MaximumDecodedPixels,
                    MaximumSampledFrames = input.Processing.AttachmentLimits.MaximumSampledFrames ?? DeploymentConfigurationDefaults.MaximumSampledFrames,
                    MaximumFrameBytes = input.Processing.AttachmentLimits.MaximumFrameBytes ?? DeploymentConfigurationDefaults.MaximumFrameBytes,
                    MaximumSelectedVideoScreenshots = input.Processing.AttachmentLimits.MaximumSelectedVideoScreenshots ?? DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots,
                    MaximumRetainedScreenshotBytes = input.Processing.AttachmentLimits.MaximumRetainedScreenshotBytes ?? DeploymentConfigurationDefaults.MaximumRetainedScreenshotBytes,
                    MaximumTranscriptCharacters = input.Processing.AttachmentLimits.MaximumTranscriptCharacters ?? DeploymentConfigurationDefaults.MaximumTranscriptCharacters,
                    MediaProcessTimeoutSeconds = input.Processing.AttachmentLimits.MediaProcessTimeoutSeconds ?? DeploymentConfigurationDefaults.MediaProcessTimeoutSeconds,
                    MaximumConcurrentMediaJobs = input.Processing.AttachmentLimits.MaximumConcurrentMediaJobs ?? DeploymentConfigurationDefaults.MaximumConcurrentMediaJobs
                }),
            new AuditConfiguration(input.Audit!.RetentionDays!.Value)
            {
                EvidenceRetentionDays = input.Audit.EvidenceRetentionDays ?? DeploymentConfigurationDefaults.EvidenceRetentionDays,
                MaximumSelectedVideoScreenshots = input.Audit.MaximumSelectedVideoScreenshots ?? DeploymentConfigurationDefaults.MaximumSelectedVideoScreenshots
            },
            exclusions);
    }

    public IntakePolicy ValidatePolicy(IntakePolicyInput input, string source = "intake policy")
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<string>();
        Require(input.Policy?.Id, "policy.id", errors);
        Require(input.Policy?.Version, "policy.version", errors);
        if (input.Criteria is null || input.Criteria.Count == 0) errors.Add("criteria must contain at least one criterion.");

        var criteria = new List<IntakeCriterion>();
        foreach (var criterion in input.Criteria ?? [])
        {
            Require(criterion.Id, "criteria[].id", errors);
            Require(criterion.DisplayName, "criteria[].displayName", errors);
            Require(criterion.Description, "criteria[].description", errors);
            Require(criterion.EvaluationGuidance, "criteria[].evaluationGuidance", errors);
            var applicability = criterion.Applicability?.Trim().ToLowerInvariant() switch
            {
                "required" => CriterionApplicability.Required,
                "contextual" => CriterionApplicability.Contextual,
                _ => (CriterionApplicability?)null
            };
            if (applicability is null) errors.Add("criteria[].applicability must be required or contextual.");
            if (criterion.Na?.Allowed is null || criterion.Na.RequiresExplanation is null)
                errors.Add("criteria[].na.allowed and criteria[].na.requiresExplanation are required booleans.");
            else if (!criterion.Na.Allowed.Value && criterion.Na.RequiresExplanation.Value)
                errors.Add("criteria[].na.requiresExplanation cannot be true when N/A is not allowed.");

            criteria.Add(new IntakeCriterion(
                criterion.Id?.Trim() ?? string.Empty,
                criterion.DisplayName?.Trim() ?? string.Empty,
                criterion.Description?.Trim() ?? string.Empty,
                applicability ?? CriterionApplicability.Required,
                new NotApplicablePolicy(criterion.Na?.Allowed ?? false, criterion.Na?.RequiresExplanation ?? false),
                criterion.EvaluationGuidance?.Trim() ?? string.Empty));
        }

        foreach (var duplicateId in criteria.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1).Select(group => group.Key))
            errors.Add($"criteria contains duplicate id '{duplicateId}'.");

        ThrowIfInvalid(source, errors);
        return new IntakePolicy(new PolicyIdentity(input.Policy!.Id!.Trim(), input.Policy.Version!.Trim()), criteria);
    }

    private static void Require(string? value, string property, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{property} is required.");
    }

    private static void EnvironmentVariableReference(string? value, string property, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var candidate = value.Trim();
        var looksLikeOpaqueCredential = candidate.Length >= 40 &&
                                        !candidate.Contains('_') &&
                                        candidate.All(char.IsLetterOrDigit);
        if (!EnvironmentVariableName.IsMatch(candidate) ||
            KnownCredentialPrefix.IsMatch(candidate) ||
            looksLikeOpaqueCredential)
            errors.Add($"{property} must be an environment-variable name, not a credential value.");
    }

    private static Uri? ParseHttpUri(string? value, string property, ICollection<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"{property} must be an absolute HTTP or HTTPS URL.");
            return null;
        }
        return uri;
    }

    private static Uri? ParseAzureDevOpsOrganizationUri(string? value, string property, ICollection<string> errors)
    {
        if (!AzureDevOpsSettingsValidation.TryNormalizeOrganization(value, out var uri))
        {
            errors.Add($"{property} must be an absolute HTTP(S) URL without user information, a query, or a fragment.");
            return null;
        }
        return uri;
    }

    private static Guid ParseGuid(string? value, string property, ICollection<string> errors)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            errors.Add($"{property} must be a non-empty GUID.");
        return parsed;
    }

    private static TimeSpan ParsePositiveTimeSpan(string? value, string property, ICollection<string> errors)
    {
        if (!TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
            errors.Add($"{property} must be a positive invariant TimeSpan (for example 1.00:00:00).");
        return parsed;
    }

    private static void Positive(long? value, string property, ICollection<string> errors)
    {
        if (value is null or <= 0) errors.Add($"{property} must be greater than zero.");
    }

    private static void NonNegative(int? value, string property, ICollection<string> errors)
    {
        if (value is null or < 0) errors.Add($"{property} must be zero or greater.");
    }

    private static void PositiveIfSpecified(long? value, string property, ICollection<string> errors)
    {
        if (value is <= 0) errors.Add($"{property} must be greater than zero when supplied.");
    }

    private static void AtMostInt32(long? value, string property, ICollection<string> errors)
    {
        if (value > int.MaxValue) errors.Add($"{property} must be no greater than {int.MaxValue}.");
    }

    private static void NonNegative(decimal? value, string property, ICollection<string> errors)
    {
        if (value is null or < 0) errors.Add($"{property} must be zero or greater.");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ThrowIfInvalid(string source, IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            throw new ConfigurationValidationException($"Invalid configuration in {source}: {string.Join(" ", errors)}");
    }
}

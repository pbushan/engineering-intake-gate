using System.Globalization;
using IntakeGate.Application.Configuration;

namespace IntakeGate.Infrastructure.Configuration;

/// <summary>
/// Maps validated runtime records to the provider-neutral document shapes shared by YAML and
/// SQLite. Credential references remain environment-variable names; credential values are not
/// representable in these documents.
/// </summary>
public static class DeploymentConfigurationDocuments
{
    public static DeploymentProfileInput Profile(DeploymentProfile profile) => new()
    {
        Profile = new ConfigurationIdentityInput { Id = profile.Identity.Id, Version = profile.Identity.Version },
        IntakePolicy = new PolicyReferenceInput { Path = profile.IntakePolicy.Path, Url = profile.IntakePolicy.Url.AbsoluteUri },
        IntakeState = new IntakeStateInput
        {
            ValidatedTag = profile.IntakeState.ValidatedTag,
            IncompleteTag = profile.IntakeState.IncompleteTag
        },
        Ado = new AzureDevOpsInput
        {
            OrganizationUrl = profile.Ado.OrganizationUrl.AbsoluteUri,
            Project = profile.Ado.Project,
            SavedQueryId = profile.Ado.SavedQueryId.ToString("D"),
            Authentication = new AzureDevOpsAuthenticationInput
            {
                PatEnvironmentVariable = profile.Ado.Authentication.EnvironmentVariable
            }
        },
        Ai = new AiInput
        {
            Provider = profile.Ai.Provider,
            Model = profile.Ai.Model,
            TimeoutSeconds = profile.Ai.TimeoutSeconds,
            Authentication = new AiAuthenticationInput
            {
                ApiKeyEnvironmentVariable = profile.Ai.Authentication.EnvironmentVariable
            },
            Pricing = profile.Ai.Pricing.Select(item => new ModelPricingInput
            {
                Provider = item.Provider,
                Model = item.Model,
                InputPerMillionTokens = item.InputPerMillionTokens,
                OutputPerMillionTokens = item.OutputPerMillionTokens,
                Currency = item.Currency,
                Identity = item.Identity
            }).ToList()
        },
        Schedule = new ScheduleInput
        {
            Enabled = profile.Schedule.Enabled,
            Expression = profile.Schedule.Expression,
            Timezone = profile.Schedule.Timezone,
            InitialLookback = profile.Schedule.InitialLookback.ToString("c", CultureInfo.InvariantCulture)
        },
        Processing = new ProcessingInput
        {
            ExecutionMode = profile.Processing.ExecutionMode == ExecutionMode.DryRun ? "DRY_RUN" : "LIVE",
            Concurrency = profile.Processing.Concurrency,
            Retries = profile.Processing.Retries,
            ContentLimits = new ContentLimitsInput
            {
                MaximumTotalCharacters = profile.Processing.ContentLimits.MaximumTotalCharacters,
                MaximumComments = profile.Processing.ContentLimits.MaximumComments,
                MaximumExtractedTextCharacters = profile.Processing.ContentLimits.MaximumExtractedTextCharacters
            },
            AttachmentLimits = new AttachmentLimitsInput
            {
                MaximumCount = profile.Processing.AttachmentLimits.MaximumCount,
                MaximumBytesPerAttachment = profile.Processing.AttachmentLimits.MaximumBytesPerAttachment,
                MaximumAggregateBytes = profile.Processing.AttachmentLimits.MaximumAggregateBytes,
                MaximumPdfPages = profile.Processing.AttachmentLimits.MaximumPdfPages,
                MaximumImageCount = profile.Processing.AttachmentLimits.MaximumImageCount,
                MaximumImageBytes = profile.Processing.AttachmentLimits.MaximumImageBytes,
                MaximumCsvRows = profile.Processing.AttachmentLimits.MaximumCsvRows,
                MaximumStructuredTextDepth = profile.Processing.AttachmentLimits.MaximumStructuredTextDepth,
                MaximumSpreadsheetSheets = profile.Processing.AttachmentLimits.MaximumSpreadsheetSheets,
                MaximumSpreadsheetRowsPerSheet = profile.Processing.AttachmentLimits.MaximumSpreadsheetRowsPerSheet,
                MaximumSpreadsheetColumns = profile.Processing.AttachmentLimits.MaximumSpreadsheetColumns,
                MaximumSpreadsheetCells = profile.Processing.AttachmentLimits.MaximumSpreadsheetCells,
                MaximumMediaDurationSeconds = profile.Processing.AttachmentLimits.MaximumMediaDurationSeconds,
                MaximumMediaDimension = profile.Processing.AttachmentLimits.MaximumMediaDimension,
                MaximumDecodedPixels = profile.Processing.AttachmentLimits.MaximumDecodedPixels,
                MaximumSampledFrames = profile.Processing.AttachmentLimits.MaximumSampledFrames,
                MaximumFrameBytes = profile.Processing.AttachmentLimits.MaximumFrameBytes,
                MaximumSelectedVideoScreenshots = profile.Processing.AttachmentLimits.MaximumSelectedVideoScreenshots,
                MaximumRetainedScreenshotBytes = profile.Processing.AttachmentLimits.MaximumRetainedScreenshotBytes,
                MaximumTranscriptCharacters = profile.Processing.AttachmentLimits.MaximumTranscriptCharacters,
                MediaProcessTimeoutSeconds = profile.Processing.AttachmentLimits.MediaProcessTimeoutSeconds,
                MaximumConcurrentMediaJobs = profile.Processing.AttachmentLimits.MaximumConcurrentMediaJobs
            }
        },
        Audit = new AuditInput
        {
            RetentionDays = profile.Audit.RetentionDays,
            EvidenceRetentionDays = profile.Audit.EvidenceRetentionDays,
            MaximumSelectedVideoScreenshots = profile.Audit.MaximumSelectedVideoScreenshots
        },
        Exclusions = profile.Exclusions.Select(item => new ExclusionInput
        {
            Id = item.Id,
            Field = item.Field,
            Operator = "equalsAny",
            Values = item.Values.ToList()
        }).ToList()
    };

    public static IntakePolicyInput Policy(IntakePolicy policy) => new()
    {
        Policy = new ConfigurationIdentityInput { Id = policy.Identity.Id, Version = policy.Identity.Version },
        Criteria = policy.Criteria.Select(item => new CriterionInput
        {
            Id = item.Id,
            DisplayName = item.DisplayName,
            Description = item.Description,
            Applicability = item.Applicability == CriterionApplicability.Required ? "required" : "contextual",
            Na = new NotApplicableInput
            {
                Allowed = item.NotApplicable.Allowed,
                RequiresExplanation = item.NotApplicable.RequiresExplanation
            },
            EvaluationGuidance = item.EvaluationGuidance
        }).ToList()
    };
}

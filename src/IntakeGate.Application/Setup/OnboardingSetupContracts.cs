using IntakeGate.Application.AiManagement;
using IntakeGate.Application.Audit;
using IntakeGate.Application.AzureDevOps;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Profiles;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.Time;

namespace IntakeGate.Application.Setup;

public sealed record OnboardingProfileDraftValues(
    string? ProfileVersion,
    string? PolicyUrl,
    OnboardingIntakeState? IntakeState,
    OnboardingAiRuntime? AiRuntime,
    OnboardingSchedule? Schedule,
    OnboardingProcessing? Processing,
    OnboardingAudit? Audit,
    IReadOnlyList<OnboardingExclusion>? Exclusions,
    OnboardingPolicy? Policy);

public sealed record OnboardingIntakeState(string? ValidatedTag, string? IncompleteTag);
public sealed record OnboardingAiRuntime(int? TimeoutSeconds, IReadOnlyList<ModelPricing>? Pricing);
public sealed record OnboardingSchedule(bool Enabled, string? Expression, string? Timezone, string? InitialLookback);
public sealed record OnboardingProcessing(string? ExecutionMode, int? Concurrency, int? Retries,
    OnboardingContentLimits? ContentLimits, OnboardingAttachmentLimits? AttachmentLimits);
public sealed record OnboardingContentLimits(int? MaximumTotalCharacters, int? MaximumComments,
    int? MaximumExtractedTextCharacters);
public sealed record OnboardingAttachmentLimits(int? MaximumCount, long? MaximumBytesPerAttachment,
    long? MaximumAggregateBytes, int? MaximumPdfPages, int? MaximumImageCount,
    long? MaximumImageBytes, int? MaximumCsvRows, int? MaximumStructuredTextDepth);
public sealed record OnboardingAudit(int? RetentionDays);
public sealed record OnboardingExclusion(string? Id, string? Field, string? Operator,
    IReadOnlyList<string>? Values);
public sealed record OnboardingPolicy(string? Id, string? Version,
    IReadOnlyList<OnboardingCriterion>? Criteria);
public sealed record OnboardingCriterion(string? Id, string? DisplayName, string? Description,
    string? Applicability, OnboardingNotApplicable? Na, string? EvaluationGuidance);
public sealed record OnboardingNotApplicable(bool? Allowed, bool? RequiresExplanation);

public sealed record OnboardingDefaults(
    OnboardingProfileDraftValues Values,
    IReadOnlyList<string> ServerSeededFields,
    IReadOnlyList<string> RequiredAdminFields);

public sealed record OnboardingProfileDraft(
    int Revision,
    OnboardingProfileDraftValues Values,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum OnboardingDraftPersistenceStatus
{
    Succeeded,
    NotFound,
    Conflict,
    InvalidConfiguration,
    ProfileAlreadyExists
}

public sealed record OnboardingDraftPersistenceResult(
    OnboardingDraftPersistenceStatus Status,
    OnboardingProfileDraft? Draft = null,
    InputValidationErrors? ValidationErrors = null);

public sealed record InputValidationErrors(
    IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SectionErrors)
{
    public bool IsEmpty => FieldErrors.Count == 0 && SectionErrors.Count == 0;
}

public interface IOnboardingProfileDraftRepository
{
    Task<OnboardingProfileDraft?> GetAsync(CancellationToken cancellationToken = default);

    Task<OnboardingDraftPersistenceResult> InitializeAsync(
        OnboardingProfileDraftValues values,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<OnboardingDraftPersistenceResult> UpdateAsync(
        int expectedRevision,
        OnboardingProfileDraftValues values,
        AuditActor actor,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public interface IScheduleConfigurationValidator
{
    bool IsValid(ScheduleConfiguration schedule);
}

public enum SetupFinalizationStatus
{
    Succeeded,
    DraftNotFound,
    Conflict,
    SetupIncomplete,
    InvalidConfiguration,
    ProductionNotAuthorized,
    RuntimeChangeInProgress
}

public sealed record SetupFinalizationResult(
    SetupFinalizationStatus Status,
    ProfileConfigurationSnapshot? Snapshot = null,
    RuntimeConfigurationGeneration? Generation = null,
    InputValidationErrors? ValidationErrors = null);

public static class AuthoritativeOnboardingDefaults
{
    public static readonly IReadOnlyList<string> ServerSeededFields =
    [
        "aiRuntime.timeoutSeconds",
        "aiRuntime.pricing",
        "schedule.enabled",
        "schedule.expression",
        "processing.executionMode",
        "processing.attachmentLimits.maximumImageCount",
        "processing.attachmentLimits.maximumImageBytes",
        "processing.attachmentLimits.maximumCsvRows",
        "processing.attachmentLimits.maximumStructuredTextDepth",
        "exclusions"
    ];

    public static readonly IReadOnlyList<string> RequiredAdminFields =
    [
        "policyUrl",
        "intakeState.validatedTag",
        "intakeState.incompleteTag",
        "schedule.timezone",
        "schedule.initialLookback",
        "processing.concurrency",
        "processing.retries",
        "processing.contentLimits.maximumTotalCharacters",
        "processing.contentLimits.maximumComments",
        "processing.contentLimits.maximumExtractedTextCharacters",
        "processing.attachmentLimits.maximumCount",
        "processing.attachmentLimits.maximumBytesPerAttachment",
        "processing.attachmentLimits.maximumAggregateBytes",
        "processing.attachmentLimits.maximumPdfPages",
        "audit.retentionDays",
        "policy.id",
        "policy.version",
        "policy.criteria[].id",
        "policy.criteria[].displayName",
        "policy.criteria[].description",
        "policy.criteria[].applicability",
        "policy.criteria[].na.allowed",
        "policy.criteria[].na.requiresExplanation",
        "policy.criteria[].evaluationGuidance"
    ];

    public static OnboardingDefaults Create() => new(
        new OnboardingProfileDraftValues(
            null,
            null,
            null,
            new OnboardingAiRuntime(DeploymentConfigurationDefaults.AiTimeoutSeconds, []),
            new OnboardingSchedule(false, string.Empty, null, null),
            new OnboardingProcessing("DRY_RUN", null, null,
                new OnboardingContentLimits(null, null, null),
                new OnboardingAttachmentLimits(null, null, null, null,
                    DeploymentConfigurationDefaults.MaximumImageCount,
                    DeploymentConfigurationDefaults.MaximumImageBytes,
                    DeploymentConfigurationDefaults.MaximumCsvRows,
                    DeploymentConfigurationDefaults.MaximumStructuredTextDepth)),
            new OnboardingAudit(null),
            [],
            null),
        ServerSeededFields,
        RequiredAdminFields);
}

public sealed class OnboardingSetupService(
    IOnboardingProfileDraftRepository drafts,
    IProfileManagementRepository profiles,
    IAzureDevOpsSetupRepository azureDevOps,
    IAiConfigurationRepository ai,
    ISecretStore secrets,
    IDeploymentConfigurationValidator configurationValidator,
    IScheduleConfigurationValidator scheduleValidator,
    IControlPlaneAuditWriter audit,
    IClock clock,
    DeploymentConfigurationState runtime)
{
    public OnboardingDefaults GetDefaults() => AuthoritativeOnboardingDefaults.Create();

    public Task<OnboardingProfileDraft?> GetDraftAsync(CancellationToken cancellationToken = default) =>
        drafts.GetAsync(cancellationToken);

    public Task<OnboardingDraftPersistenceResult> InitializeDraftAsync(
        AuditActor actor, CancellationToken cancellationToken = default) =>
        drafts.InitializeAsync(GetDefaults().Values, actor, clock.UtcNow.ToUniversalTime(), cancellationToken);

    public async Task<OnboardingDraftPersistenceResult> UpdateDraftAsync(
        int expectedRevision,
        OnboardingProfileDraftValues values,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision <= 0 || !IsSupportedDraft(values))
            return new(OnboardingDraftPersistenceStatus.InvalidConfiguration,
                ValidationErrors: ValidateDraft(values));
        return await drafts.UpdateAsync(expectedRevision, Normalize(values), actor,
            clock.UtcNow.ToUniversalTime(), cancellationToken);
    }

    public async Task<SetupFinalizationResult> FinalizeAsync(
        int expectedDraftRevision,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var draft = await drafts.GetAsync(cancellationToken);
        if (draft is null) return new(SetupFinalizationStatus.DraftNotFound);
        if (draft.Revision != expectedDraftRevision) return new(SetupFinalizationStatus.Conflict);
        if (!TryBuildEditable(draft.Values, out var editable))
            return new(SetupFinalizationStatus.InvalidConfiguration,
                ValidationErrors: ValidateDraft(draft.Values));

        var ado = await azureDevOps.GetAsync(cancellationToken);
        var aiState = await ai.GetAsync(cancellationToken);
        if (ado?.SavedQueryId is null || ado.QueryValidatedAtUtc is null ||
            string.IsNullOrWhiteSpace(ado.ConfigurationFingerprint) ||
            aiState.ProfileConfigured || !aiState.ModelConfirmed ||
            string.IsNullOrWhiteSpace(aiState.Provider) || string.IsNullOrWhiteSpace(aiState.Model) ||
            aiState.CredentialUpdatedAtUtc is null || aiState.ModelValidatedAtUtc is null)
            return new(SetupFinalizationStatus.SetupIncomplete);

        var adoCredential = await secrets.GetMetadataAsync(CredentialSlot.AzureDevOps, cancellationToken);
        var aiSlot = AiProviderNames.CredentialSlot(aiState.Provider);
        var aiCredential = await secrets.GetMetadataAsync(aiSlot, cancellationToken);
        if (!Ready(adoCredential) || adoCredential.UpdatedAtUtc > ado.QueryValidatedAtUtc ||
            !Ready(aiCredential) || aiCredential.UpdatedAtUtc != aiState.CredentialUpdatedAtUtc)
            return new(SetupFinalizationStatus.SetupIncomplete);
        if (editable!.Processing.ExecutionMode == ExecutionMode.Live)
        {
            await audit.AppendAsync(new ControlPlaneAuditRecord(Guid.NewGuid(), clock.UtcNow.ToUniversalTime(), actor,
                "OnboardingProductionModeRejected", "OnboardingProfileDraft", "singleton",
                ["executionMode", "releasePosture"]), cancellationToken);
            return new(SetupFinalizationStatus.ProductionNotAuthorized);
        }

        DeploymentConfiguration candidate;
        try
        {
            candidate = configurationValidator.ValidateConfiguration(new DeploymentConfiguration(
                new DeploymentProfile(
                    new ProfileIdentity(Guid.NewGuid().ToString("D"), editable.ProfileVersion),
                    new IntakePolicyReference(ProfileManagementService.ManagedPolicyPath, editable.PolicyUrl),
                    editable.IntakeState,
                    new AzureDevOpsConfiguration(ado.OrganizationUrl, ado.Project, ado.SavedQueryId.Value,
                        new CredentialReference(ProfileManagementService.AzureDevOpsCredentialSlotReference)),
                    new AiConfiguration(aiState.Provider, aiState.Model, new CredentialReference(
                        aiState.Provider == AiProviderNames.OpenAi
                            ? ProfileManagementService.OpenAiCredentialSlotReference
                            : ProfileManagementService.AnthropicCredentialSlotReference))
                    {
                        TimeoutSeconds = editable.AiTimeoutSeconds,
                        Pricing = editable.AiPricing
                    },
                    editable.Schedule,
                    editable.Processing,
                    editable.Audit,
                    editable.Exclusions),
                editable.Policy,
                PolicyFingerprint.Create(editable.Policy)),
                "onboarding finalization", "onboarding policy");
        }
        catch (ConfigurationValidationException)
        {
            return new(SetupFinalizationStatus.InvalidConfiguration);
        }

        if (!runtime.TryBeginManagementChange())
            return new(SetupFinalizationStatus.RuntimeChangeInProgress);
        try
        {
            var persisted = await profiles.FinalizeFromOnboardingDraftAsync(
                candidate, ado, aiState, expectedDraftRevision,
                Binding(CredentialSlot.AzureDevOps, adoCredential), Binding(aiSlot, aiCredential),
                actor, clock.UtcNow.ToUniversalTime(), cancellationToken);
            if (persisted.Status == ProfilePersistenceStatus.Committed && persisted.Generation is not null)
                runtime.Activate(persisted.Generation);
            runtime.CompleteManagementChange(false);
            return persisted.Status switch
            {
                ProfilePersistenceStatus.Committed => new(SetupFinalizationStatus.Succeeded,
                    persisted.Snapshot, persisted.Generation),
                ProfilePersistenceStatus.SetupIncomplete => new(SetupFinalizationStatus.SetupIncomplete),
                _ => new(SetupFinalizationStatus.Conflict)
            };
        }
        catch
        {
            runtime.CompleteManagementChange(false);
            throw;
        }
    }

    public bool IsDraftComplete(OnboardingProfileDraft? draft) =>
        IsProfileDetailsComplete(draft) && IsPolicyDetailsComplete(draft) &&
        TryBuildEditable(draft!.Values, out _);

    public InputValidationErrors ValidateDraft(OnboardingProfileDraftValues values)
    {
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var sections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        static void Add(IDictionary<string, IReadOnlyList<string>> target, string key, string message) =>
            target[key] = target.TryGetValue(key, out var existing) ? [.. existing, message] : [message];
        static bool Missing(string? value) => string.IsNullOrWhiteSpace(value);
        static void Required(IDictionary<string, IReadOnlyList<string>> target, string key, string? value, string label)
        {
            if (Missing(value)) Add(target, key, $"{label} is required.");
        }
        static void Positive(IDictionary<string, IReadOnlyList<string>> target, string key, long? value, string label)
        {
            if (value is null) Add(target, key, $"{label} is required.");
            else if (value <= 0) Add(target, key, $"{label} must be greater than zero.");
        }

        Required(fields, "policyUrl", values.PolicyUrl, "Policy URL");
        if (!Missing(values.PolicyUrl) &&
            (!Uri.TryCreate(values.PolicyUrl, UriKind.Absolute, out var policyUrl) ||
             policyUrl.Scheme is not ("http" or "https")))
            Add(fields, "policyUrl", "Enter an absolute HTTP or HTTPS policy URL.");
        Required(fields, "intakeState.validatedTag", values.IntakeState?.ValidatedTag, "Engineering Ready tag");
        Required(fields, "intakeState.incompleteTag", values.IntakeState?.IncompleteTag, "Intake Incomplete tag");
        if (!Missing(values.IntakeState?.ValidatedTag) &&
            string.Equals(values.IntakeState!.ValidatedTag!.Trim(), values.IntakeState.IncompleteTag?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            Add(fields, "intakeState.validatedTag", "Use different values for the two intake tags.");
            Add(fields, "intakeState.incompleteTag", "Use different values for the two intake tags.");
        }

        Required(fields, "schedule.timezone", values.Schedule?.Timezone, "Timezone");
        if (!Missing(values.Schedule?.Timezone))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(values.Schedule!.Timezone!.Trim());
                var schedule = new ScheduleConfiguration(values.Schedule.Enabled,
                    values.Schedule.Expression?.Trim() ?? string.Empty, values.Schedule.Timezone.Trim(),
                    TimeSpan.FromTicks(1));
                if (values.Schedule.Enabled && !Missing(values.Schedule.Expression) &&
                    !scheduleValidator.IsValid(schedule))
                    Add(fields, "schedule.expression",
                        "Enter a valid six-field cron expression for the selected timezone.");
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                Add(fields, "schedule.timezone", "Select a backend-supported timezone.");
            }
        }
        if (values.Schedule?.Enabled == true)
            Required(fields, "schedule.expression", values.Schedule.Expression, "Schedule expression");
        Required(fields, "schedule.initialLookback", values.Schedule?.InitialLookback, "Initial lookback");
        if (!Missing(values.Schedule?.InitialLookback) &&
            (!TimeSpan.TryParseExact(values.Schedule!.InitialLookback, "c",
                System.Globalization.CultureInfo.InvariantCulture, out var lookback) || lookback <= TimeSpan.Zero))
            Add(fields, "schedule.initialLookback", "Enter a positive .NET duration, for example 7.00:00:00.");

        Positive(fields, "processing.concurrency", values.Processing?.Concurrency, "Concurrency");
        if (values.Processing?.Retries is null) Add(fields, "processing.retries", "Retries is required.");
        else if (values.Processing.Retries < 0) Add(fields, "processing.retries", "Retries must be zero or greater.");
        Positive(fields, "processing.contentLimits.maximumTotalCharacters",
            values.Processing?.ContentLimits?.MaximumTotalCharacters, "Maximum total characters");
        Positive(fields, "processing.contentLimits.maximumComments",
            values.Processing?.ContentLimits?.MaximumComments, "Maximum comments");
        Positive(fields, "processing.contentLimits.maximumExtractedTextCharacters",
            values.Processing?.ContentLimits?.MaximumExtractedTextCharacters, "Maximum extracted text characters");
        Positive(fields, "processing.attachmentLimits.maximumCount",
            values.Processing?.AttachmentLimits?.MaximumCount, "Maximum attachments");
        Positive(fields, "processing.attachmentLimits.maximumBytesPerAttachment",
            values.Processing?.AttachmentLimits?.MaximumBytesPerAttachment, "Bytes per attachment");
        if (values.Processing?.AttachmentLimits?.MaximumBytesPerAttachment > int.MaxValue)
            Add(fields, "processing.attachmentLimits.maximumBytesPerAttachment",
                $"Bytes per attachment must be no greater than {int.MaxValue}.");
        Positive(fields, "processing.attachmentLimits.maximumAggregateBytes",
            values.Processing?.AttachmentLimits?.MaximumAggregateBytes, "Aggregate attachment bytes");
        Positive(fields, "processing.attachmentLimits.maximumPdfPages",
            values.Processing?.AttachmentLimits?.MaximumPdfPages, "Maximum PDF pages");
        Positive(fields, "processing.attachmentLimits.maximumImageCount",
            values.Processing?.AttachmentLimits?.MaximumImageCount, "Maximum images");
        Positive(fields, "processing.attachmentLimits.maximumImageBytes",
            values.Processing?.AttachmentLimits?.MaximumImageBytes, "Maximum image bytes");
        if (values.Processing?.AttachmentLimits?.MaximumImageBytes > int.MaxValue)
            Add(fields, "processing.attachmentLimits.maximumImageBytes",
                $"Maximum image bytes must be no greater than {int.MaxValue}.");
        Positive(fields, "processing.attachmentLimits.maximumCsvRows",
            values.Processing?.AttachmentLimits?.MaximumCsvRows, "Maximum CSV rows");
        Positive(fields, "processing.attachmentLimits.maximumStructuredTextDepth",
            values.Processing?.AttachmentLimits?.MaximumStructuredTextDepth, "Structured text depth");
        Positive(fields, "aiRuntime.timeoutSeconds", values.AiRuntime?.TimeoutSeconds, "AI timeout");
        Positive(fields, "audit.retentionDays", values.Audit?.RetentionDays, "Retention days");

        Required(fields, "policy.id", values.Policy?.Id, "Policy ID");
        Required(fields, "policy.version", values.Policy?.Version, "Policy version");
        if (values.Policy?.Criteria is not { Count: > 0 })
            Add(sections, "policy.criteria", "Add at least one intake criterion.");
        else
        {
            for (var index = 0; index < values.Policy.Criteria.Count; index++)
            {
                var criterion = values.Policy.Criteria[index];
                var prefix = $"policy.criteria[{index}]";
                Required(fields, $"{prefix}.id", criterion.Id, $"Criterion {index + 1} ID");
                Required(fields, $"{prefix}.displayName", criterion.DisplayName, $"Criterion {index + 1} display name");
                Required(fields, $"{prefix}.description", criterion.Description, $"Criterion {index + 1} description");
                Required(fields, $"{prefix}.evaluationGuidance", criterion.EvaluationGuidance,
                    $"Criterion {index + 1} evaluation guidance");
                if (criterion.Applicability?.Trim().ToLowerInvariant() is not ("required" or "contextual"))
                    Add(fields, $"{prefix}.applicability", "Select Required or Contextual.");
                if (criterion.Na?.Allowed is null || criterion.Na.RequiresExplanation is null)
                    Add(fields, $"{prefix}.na", "Choose valid not-applicable settings.");
                else if (!criterion.Na.Allowed.Value && criterion.Na.RequiresExplanation.Value)
                    Add(fields, $"{prefix}.na", "An explanation cannot be required when N/A is not allowed.");
            }
            foreach (var duplicate in values.Policy.Criteria
                         .Select((item, index) => (item.Id, index))
                         .Where(item => !Missing(item.Id))
                         .GroupBy(item => item.Id!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
                foreach (var item in duplicate)
                    Add(fields, $"policy.criteria[{item.index}].id", "Criterion IDs must be unique.");
        }

        if (values.AiRuntime?.Pricing is null || values.AiRuntime.Pricing.Any(item =>
                Missing(item.Provider) || Missing(item.Model) || Missing(item.Currency) ||
                item.InputPerMillionTokens < 0 || item.OutputPerMillionTokens < 0) ||
            values.AiRuntime?.Pricing?.GroupBy(item => $"{item.Provider}\0{item.Model}",
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) == true)
            Add(sections, "aiRuntime.pricing", "Review the model-pricing entries.");
        if (values.Exclusions is null || values.Exclusions.Any(item => Missing(item.Id) || Missing(item.Field) ||
                !string.Equals(item.Operator, "equalsAny", StringComparison.OrdinalIgnoreCase) ||
                item.Values is null || item.Values.Count == 0 || item.Values.Any(Missing)))
            Add(sections, "exclusions", "Review the exclusion rules and provide a field and at least one value.");

        return new InputValidationErrors(fields, sections);
    }

    public bool IsProfileDetailsComplete(OnboardingProfileDraft? draft) =>
        draft is not null && IsSupportedDraft(draft.Values) && ProfileFieldsPresent(draft.Values);

    public bool IsPolicyDetailsComplete(OnboardingProfileDraft? draft)
    {
        if (draft?.Values.Policy is not { Criteria.Count: > 0 } policy) return false;
        try
        {
            var value = new IntakePolicy(
                new PolicyIdentity(policy.Id?.Trim() ?? string.Empty, policy.Version?.Trim() ?? string.Empty),
                policy.Criteria.Select(item => new IntakeCriterion(
                    item.Id?.Trim() ?? string.Empty,
                    item.DisplayName?.Trim() ?? string.Empty,
                    item.Description?.Trim() ?? string.Empty,
                    item.Applicability?.Trim().ToLowerInvariant() == "contextual"
                        ? CriterionApplicability.Contextual : CriterionApplicability.Required,
                    new NotApplicablePolicy(item.Na?.Allowed ?? false, item.Na?.RequiresExplanation ?? false),
                    item.EvaluationGuidance?.Trim() ?? string.Empty)).ToArray());
            configurationValidator.ValidatePolicy(DeploymentConfigurationInputs.Policy(value),
                "onboarding draft policy");
            return true;
        }
        catch (ConfigurationValidationException)
        {
            return false;
        }
    }

    private bool IsSupportedDraft(OnboardingProfileDraftValues values)
    {
        if (values.AiRuntime?.TimeoutSeconds is null or <= 0 || values.AiRuntime.Pricing is null ||
            values.Schedule is null || values.Processing?.ExecutionMode is null ||
            values.Processing.ContentLimits is null || values.Processing.AttachmentLimits is null ||
            values.Audit is null || values.Exclusions is null)
            return false;
        if (values.PolicyUrl is not null &&
            (!Uri.TryCreate(values.PolicyUrl, UriKind.Absolute, out var policyUrl) ||
             policyUrl.Scheme is not ("http" or "https")))
            return false;
        if (values.IntakeState is not null &&
            ((values.IntakeState.ValidatedTag is not null &&
              string.IsNullOrWhiteSpace(values.IntakeState.ValidatedTag)) ||
             (values.IntakeState.IncompleteTag is not null &&
              string.IsNullOrWhiteSpace(values.IntakeState.IncompleteTag)) ||
             (!string.IsNullOrWhiteSpace(values.IntakeState.ValidatedTag) &&
              string.Equals(values.IntakeState.ValidatedTag.Trim(), values.IntakeState.IncompleteTag?.Trim(),
                  StringComparison.OrdinalIgnoreCase))))
            return false;
        if (values.AiRuntime.Pricing.Any(item =>
                string.IsNullOrWhiteSpace(item.Provider) || string.IsNullOrWhiteSpace(item.Model) ||
                string.IsNullOrWhiteSpace(item.Currency) || item.InputPerMillionTokens < 0 ||
                item.OutputPerMillionTokens < 0) ||
            values.AiRuntime.Pricing.GroupBy(item => $"{item.Provider}\0{item.Model}",
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return false;
        if (values.Schedule.Enabled && string.IsNullOrWhiteSpace(values.Schedule.Expression)) return false;
        if (values.Schedule.InitialLookback is not null &&
            (!TimeSpan.TryParseExact(values.Schedule.InitialLookback, "c",
                System.Globalization.CultureInfo.InvariantCulture, out var lookback) || lookback <= TimeSpan.Zero))
            return false;
        if (!string.IsNullOrWhiteSpace(values.Schedule.Timezone))
        {
            var candidate = new ScheduleConfiguration(values.Schedule.Enabled,
                values.Schedule.Expression?.Trim() ?? string.Empty, values.Schedule.Timezone.Trim(),
                TimeSpan.FromTicks(1));
            if (!scheduleValidator.IsValid(candidate)) return false;
        }
        if (values.Processing.ExecutionMode.Trim().ToUpperInvariant() is not ("DRY_RUN" or "LIVE") ||
            values.Processing.Concurrency is <= 0 || values.Processing.Retries is < 0 ||
            values.Processing.ContentLimits.MaximumTotalCharacters is <= 0 ||
            values.Processing.ContentLimits.MaximumComments is <= 0 ||
            values.Processing.ContentLimits.MaximumExtractedTextCharacters is <= 0 ||
            values.Processing.AttachmentLimits.MaximumCount is <= 0 ||
            values.Processing.AttachmentLimits.MaximumBytesPerAttachment is <= 0 or > int.MaxValue ||
            values.Processing.AttachmentLimits.MaximumAggregateBytes is <= 0 ||
            values.Processing.AttachmentLimits.MaximumPdfPages is <= 0 ||
            values.Processing.AttachmentLimits.MaximumImageCount is null or <= 0 ||
            values.Processing.AttachmentLimits.MaximumImageBytes is null or <= 0 or > int.MaxValue ||
            values.Processing.AttachmentLimits.MaximumCsvRows is null or <= 0 ||
            values.Processing.AttachmentLimits.MaximumStructuredTextDepth is null or <= 0 ||
            values.Audit.RetentionDays is <= 0)
            return false;
        if (values.Exclusions.Any(item => string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Field) ||
                !string.Equals(item.Operator, "equalsAny", StringComparison.OrdinalIgnoreCase) ||
                item.Values is null || item.Values.Count == 0 || item.Values.Any(string.IsNullOrWhiteSpace)))
            return false;
        if (values.Policy?.Criteria is { } criteria &&
            (criteria.Any(item =>
                 (item.Applicability is not null &&
                  item.Applicability.Trim().ToLowerInvariant() is not ("required" or "contextual")) ||
                 (item.Na is { Allowed: false, RequiresExplanation: true })) ||
             criteria.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                 .GroupBy(item => item.Id!.Trim(), StringComparer.OrdinalIgnoreCase)
                 .Any(group => group.Count() > 1)))
            return false;
        return true;
    }

    private static bool ProfileFieldsPresent(OnboardingProfileDraftValues values) =>
        !string.IsNullOrWhiteSpace(values.PolicyUrl) &&
        !string.IsNullOrWhiteSpace(values.IntakeState?.ValidatedTag) &&
        !string.IsNullOrWhiteSpace(values.IntakeState?.IncompleteTag) &&
        !string.IsNullOrWhiteSpace(values.Schedule?.Timezone) &&
        !string.IsNullOrWhiteSpace(values.Schedule.InitialLookback) &&
        values.Processing?.Concurrency is not null && values.Processing.Retries is not null &&
        values.Processing.ContentLimits?.MaximumTotalCharacters is not null &&
        values.Processing.ContentLimits.MaximumComments is not null &&
        values.Processing.ContentLimits.MaximumExtractedTextCharacters is not null &&
        values.Processing.AttachmentLimits?.MaximumCount is not null &&
        values.Processing.AttachmentLimits.MaximumBytesPerAttachment is not null &&
        values.Processing.AttachmentLimits.MaximumAggregateBytes is not null &&
        values.Processing.AttachmentLimits.MaximumPdfPages is not null &&
        values.Audit?.RetentionDays is not null;

    public bool TryBuildEditable(OnboardingProfileDraftValues values, out ProfileEditableConfiguration? editable)
    {
        editable = null;
        if (values.IntakeState is null || values.AiRuntime is null || values.Schedule is null ||
            values.Processing?.ContentLimits is null || values.Processing.AttachmentLimits is null ||
            values.Audit is null || values.Policy?.Criteria is null || values.Policy.Criteria.Count == 0 ||
            !Uri.TryCreate(values.PolicyUrl, UriKind.Absolute, out var policyUrl) ||
            policyUrl.Scheme is not ("http" or "https") ||
            !TimeSpan.TryParseExact(values.Schedule.InitialLookback, "c",
                System.Globalization.CultureInfo.InvariantCulture, out var lookback) || lookback <= TimeSpan.Zero)
            return false;
        var mode = values.Processing.ExecutionMode?.Trim().ToUpperInvariant() switch
        {
            "DRY_RUN" => ExecutionMode.DryRun,
            "LIVE" => ExecutionMode.Live,
            _ => (ExecutionMode?)null
        };
        if (mode is null || values.Policy.Criteria.Any(item =>
                item.Na?.Allowed is null || item.Na.RequiresExplanation is null ||
                item.Applicability?.Trim().ToLowerInvariant() is not ("required" or "contextual")) ||
            values.Exclusions?.Any(item => !string.Equals(item.Operator, "equalsAny",
                StringComparison.OrdinalIgnoreCase)) == true)
            return false;
        if (!IsSupportedDraft(values))
            return false;
        var schedule = new ScheduleConfiguration(values.Schedule.Enabled,
            values.Schedule.Expression?.Trim() ?? string.Empty,
            values.Schedule.Timezone?.Trim() ?? string.Empty, lookback);
        if (!scheduleValidator.IsValid(schedule)) return false;
        var policy = new IntakePolicy(
            new PolicyIdentity(values.Policy.Id?.Trim() ?? string.Empty, values.Policy.Version?.Trim() ?? string.Empty),
            values.Policy.Criteria.Select(item => new IntakeCriterion(
                item.Id?.Trim() ?? string.Empty,
                item.DisplayName?.Trim() ?? string.Empty,
                item.Description?.Trim() ?? string.Empty,
                item.Applicability?.Trim().ToLowerInvariant() == "contextual"
                    ? CriterionApplicability.Contextual : CriterionApplicability.Required,
                new NotApplicablePolicy(item.Na?.Allowed ?? false, item.Na?.RequiresExplanation ?? false),
                item.EvaluationGuidance?.Trim() ?? string.Empty)).ToArray());
        editable = new ProfileEditableConfiguration(
            string.IsNullOrWhiteSpace(values.ProfileVersion) ? null : values.ProfileVersion.Trim(),
            policyUrl,
            new IntakeStateConfiguration(values.IntakeState.ValidatedTag?.Trim() ?? string.Empty,
                values.IntakeState.IncompleteTag?.Trim() ?? string.Empty),
            values.AiRuntime.TimeoutSeconds ?? 0,
            values.AiRuntime.Pricing ?? [],
            schedule,
            new ProcessingConfiguration(mode.Value,
                values.Processing.Concurrency ?? 0,
                values.Processing.Retries ?? -1,
                new ContentLimits(values.Processing.ContentLimits.MaximumTotalCharacters ?? 0,
                    values.Processing.ContentLimits.MaximumComments ?? 0,
                    values.Processing.ContentLimits.MaximumExtractedTextCharacters ?? 0),
                new AttachmentLimits(values.Processing.AttachmentLimits.MaximumCount ?? 0,
                    values.Processing.AttachmentLimits.MaximumBytesPerAttachment ?? 0,
                    values.Processing.AttachmentLimits.MaximumAggregateBytes ?? 0,
                    values.Processing.AttachmentLimits.MaximumPdfPages ?? 0,
                    values.Processing.AttachmentLimits.MaximumImageCount ?? 0,
                    values.Processing.AttachmentLimits.MaximumImageBytes ?? 0,
                    values.Processing.AttachmentLimits.MaximumCsvRows ?? 0,
                    values.Processing.AttachmentLimits.MaximumStructuredTextDepth ?? 0)),
            new AuditConfiguration(values.Audit.RetentionDays ?? 0),
            values.Exclusions?.Select(item => new ExclusionRule(item.Id?.Trim() ?? string.Empty,
                item.Field?.Trim() ?? string.Empty, ExclusionOperator.EqualsAny,
                item.Values?.Select(value => value.Trim()).ToArray() ?? [])).ToArray() ?? [],
            policy);
        try
        {
            // Validate policy now without manufacturing integration values. Complete profile validation
            // runs after confirmed ADO and AI state is assembled during finalization.
            configurationValidator.ValidatePolicy(DeploymentConfigurationInputs.Policy(policy),
                "onboarding draft policy");
            if (editable.AiTimeoutSeconds <= 0 || editable.Processing.Concurrency <= 0 ||
                editable.Processing.Retries < 0 || editable.Audit.RetentionDays <= 0 ||
                string.IsNullOrWhiteSpace(editable.IntakeState.ValidatedTag) ||
                string.IsNullOrWhiteSpace(editable.IntakeState.IncompleteTag) ||
                string.Equals(editable.IntakeState.ValidatedTag, editable.IntakeState.IncompleteTag,
                    StringComparison.OrdinalIgnoreCase) || !ValidLimits(editable.Processing))
                return false;
        }
        catch (ConfigurationValidationException)
        {
            return false;
        }
        return true;
    }

    private static bool ValidLimits(ProcessingConfiguration processing) =>
        processing.ContentLimits.MaximumTotalCharacters > 0 &&
        processing.ContentLimits.MaximumComments > 0 &&
        processing.ContentLimits.MaximumExtractedTextCharacters > 0 &&
        processing.AttachmentLimits.MaximumCount > 0 &&
        processing.AttachmentLimits.MaximumBytesPerAttachment is > 0 and <= int.MaxValue &&
        processing.AttachmentLimits.MaximumAggregateBytes > 0 &&
        processing.AttachmentLimits.MaximumPdfPages > 0 &&
        processing.AttachmentLimits.MaximumImageCount > 0 &&
        processing.AttachmentLimits.MaximumImageBytes is > 0 and <= int.MaxValue &&
        processing.AttachmentLimits.MaximumCsvRows > 0 &&
        processing.AttachmentLimits.MaximumStructuredTextDepth > 0;

    private static bool Ready(CredentialMetadata metadata) =>
        metadata.Configured && metadata.SourceKind is not null && metadata.UpdatedAtUtc is not null &&
        metadata.VerificationStatus == CredentialVerificationStatus.Verified;

    private static RuntimeCredentialBinding Binding(CredentialSlot slot, CredentialMetadata metadata) =>
        new(slot, metadata.SourceKind!.Value, metadata.UpdatedAtUtc!.Value);

    private static OnboardingProfileDraftValues Normalize(OnboardingProfileDraftValues values) => values with
    {
        ProfileVersion = string.IsNullOrWhiteSpace(values.ProfileVersion) ? null : values.ProfileVersion.Trim(),
        PolicyUrl = values.PolicyUrl?.Trim()
    };
}

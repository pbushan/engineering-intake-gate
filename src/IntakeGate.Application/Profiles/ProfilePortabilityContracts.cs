using IntakeGate.Application.Configuration;
using IntakeGate.Application.Setup;

namespace IntakeGate.Application.Profiles;

/// <summary>Versioned, secret-free external contract for portable Profile &amp; Policy configuration.</summary>
public sealed record PortableProfileDocument(
    string Format,
    int Version,
    DateTimeOffset ExportedAt,
    PortableProfile Profile,
    OnboardingPolicy Policy);

public sealed record PortableProfile(
    string? ProfileVersion,
    string? PolicyUrl,
    OnboardingIntakeState? IntakeState,
    OnboardingAiRuntime? AiRuntime,
    OnboardingSchedule? Schedule,
    OnboardingProcessing? Processing,
    OnboardingAudit? Audit,
    IReadOnlyList<OnboardingExclusion>? Exclusions);

public static class ProfilePortability
{
    public const string Format = "engineering-intake-gate-profile";
    public const int Version = 1;

    public static PortableProfileDocument Export(OnboardingProfileDraftValues values, DateTimeOffset exportedAt) =>
        new(Format, Version, exportedAt.ToUniversalTime(), new PortableProfile(
            values.ProfileVersion, values.PolicyUrl, values.IntakeState, values.AiRuntime, values.Schedule,
            values.Processing, values.Audit, values.Exclusions), values.Policy ?? new OnboardingPolicy(null, null, null));

    public static PortableProfileDocument Export(DeploymentConfiguration configuration, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profile = configuration.Profile;
        var values = new OnboardingProfileDraftValues(
            profile.Identity.Version,
            profile.IntakePolicy.Url.AbsoluteUri,
            new OnboardingIntakeState(profile.IntakeState.ValidatedTag, profile.IntakeState.IncompleteTag),
            new OnboardingAiRuntime(profile.Ai.TimeoutSeconds, profile.Ai.Pricing),
            new OnboardingSchedule(profile.Schedule.Enabled, profile.Schedule.Expression,
                profile.Schedule.Timezone, profile.Schedule.InitialLookback.ToString("c", System.Globalization.CultureInfo.InvariantCulture)),
            new OnboardingProcessing(profile.Processing.ExecutionMode == ExecutionMode.DryRun ? "DRY_RUN" : "LIVE",
                profile.Processing.Concurrency, profile.Processing.Retries,
                new OnboardingContentLimits(profile.Processing.ContentLimits.MaximumTotalCharacters,
                    profile.Processing.ContentLimits.MaximumComments,
                    profile.Processing.ContentLimits.MaximumExtractedTextCharacters),
                new OnboardingAttachmentLimits(profile.Processing.AttachmentLimits.MaximumCount,
                    profile.Processing.AttachmentLimits.MaximumBytesPerAttachment,
                    profile.Processing.AttachmentLimits.MaximumAggregateBytes,
                    profile.Processing.AttachmentLimits.MaximumPdfPages,
                    profile.Processing.AttachmentLimits.MaximumImageCount,
                    profile.Processing.AttachmentLimits.MaximumImageBytes,
                    profile.Processing.AttachmentLimits.MaximumCsvRows,
                    profile.Processing.AttachmentLimits.MaximumStructuredTextDepth)),
            new OnboardingAudit(profile.Audit.RetentionDays),
            profile.Exclusions.Select(item => new OnboardingExclusion(item.Id, item.Field, "equalsAny", item.Values)).ToArray(),
            new OnboardingPolicy(configuration.Policy.Identity.Id, configuration.Policy.Identity.Version,
                configuration.Policy.Criteria.Select(item => new OnboardingCriterion(item.Id, item.DisplayName,
                    item.Description, item.Applicability == CriterionApplicability.Required ? "required" : "contextual",
                    new OnboardingNotApplicable(item.NotApplicable.Allowed, item.NotApplicable.RequiresExplanation),
                    item.EvaluationGuidance)).ToArray()));
        return Export(values, exportedAt);
    }

    public static OnboardingProfileDraftValues ToDraft(PortableProfileDocument document) => new(
        document.Profile.ProfileVersion,
        document.Profile.PolicyUrl,
        document.Profile.IntakeState,
        document.Profile.AiRuntime,
        document.Profile.Schedule,
        document.Profile.Processing,
        document.Profile.Audit,
        document.Profile.Exclusions,
        document.Policy);
}

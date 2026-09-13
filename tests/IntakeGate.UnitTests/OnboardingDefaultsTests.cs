using IntakeGate.Application.Configuration;
using IntakeGate.Application.Setup;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class OnboardingDefaultsTests
{
    [Fact]
    public void SETUP_4B0_DefaultsUseOnlyEstablishedBackendSemanticsAndLeaveOtherValuesRequired()
    {
        var defaults = AuthoritativeOnboardingDefaults.Create();

        Assert.Equal(DeploymentConfigurationDefaults.AiTimeoutSeconds,
            defaults.Values.AiRuntime!.TimeoutSeconds);
        Assert.Equal("DRY_RUN", defaults.Values.Processing!.ExecutionMode);
        Assert.False(defaults.Values.Schedule!.Enabled);
        Assert.Equal(DeploymentConfigurationDefaults.MaximumImageCount,
            defaults.Values.Processing.AttachmentLimits!.MaximumImageCount);
        Assert.Equal(DeploymentConfigurationDefaults.MaximumImageBytes,
            defaults.Values.Processing.AttachmentLimits.MaximumImageBytes);
        Assert.Equal(DeploymentConfigurationDefaults.MaximumCsvRows,
            defaults.Values.Processing.AttachmentLimits.MaximumCsvRows);
        Assert.Equal(DeploymentConfigurationDefaults.MaximumStructuredTextDepth,
            defaults.Values.Processing.AttachmentLimits.MaximumStructuredTextDepth);

        Assert.Null(defaults.Values.PolicyUrl);
        Assert.Null(defaults.Values.IntakeState);
        Assert.Null(defaults.Values.Processing.Concurrency);
        Assert.Null(defaults.Values.Processing.Retries);
        Assert.Null(defaults.Values.Audit!.RetentionDays);
        Assert.Null(defaults.Values.Policy);
        Assert.Contains("policy.criteria[].evaluationGuidance", defaults.RequiredAdminFields);
        Assert.DoesNotContain(defaults.ServerSeededFields,
            field => field.Contains("example", StringComparison.OrdinalIgnoreCase));
    }
}

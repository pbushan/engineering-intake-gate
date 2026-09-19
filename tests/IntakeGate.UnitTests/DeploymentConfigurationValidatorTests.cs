using IntakeGate.Application.Configuration;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class DeploymentConfigurationValidatorTests
{
    [Fact]
    public void CFG_001_CFG_003_SharedValidatorCreatesNormalizedConfigurationWithoutInfrastructure()
    {
        var validator = new DeploymentConfigurationValidator();

        var configuration = validator.Validate(ValidProfile(), ValidPolicy(), "memory profile", "memory policy");

        Assert.Equal("profile-id", configuration.Profile.Identity.Id);
        Assert.Equal("openai", configuration.Profile.Ai.Provider);
        Assert.Equal("USD", configuration.Profile.Ai.Pricing.Single().Currency);
        Assert.Equal(ExecutionMode.DryRun, configuration.Profile.Processing.ExecutionMode);
        Assert.Equal(30, configuration.Profile.Audit.EvidenceRetentionDays);
        Assert.Equal(6, configuration.Profile.Audit.MaximumSelectedVideoScreenshots);
        Assert.Equal("policy-id", configuration.Policy.Identity.Id);
        Assert.StartsWith("sha256:", configuration.PolicyFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void CFG_003_SharedValidatorPreservesInvalidPolicySemantics()
    {
        var policy = ValidPolicy();
        policy.Criteria![0] = new CriterionInput
        {
            Id = "criterion",
            DisplayName = "Criterion",
            Description = "Description",
            Applicability = "required",
            Na = new NotApplicableInput { Allowed = false, RequiresExplanation = true },
            EvaluationGuidance = "Guidance"
        };

        var error = Assert.Throws<ConfigurationValidationException>(
            () => new DeploymentConfigurationValidator().Validate(ValidProfile(), policy, "memory profile", "memory policy"));

        Assert.Contains("criteria[].na.requiresExplanation cannot be true", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CFG_001_SharedValidatorPreservesInvalidProfileSemantics()
    {
        var profile = ValidProfile("same", "SAME");

        var error = Assert.Throws<ConfigurationValidationException>(
            () => new DeploymentConfigurationValidator().Validate(profile, ValidPolicy(), "memory profile", "memory policy"));

        Assert.Contains("intakeState.validatedTag and intakeState.incompleteTag must be different", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CFG_001_EvidenceRetentionAndFutureScreenshotLimitsAreBounded()
    {
        var profile = ValidProfile(evidenceRetentionDays: 3651, maximumSelectedVideoScreenshots: 7);

        var error = Assert.Throws<ConfigurationValidationException>(() =>
            new DeploymentConfigurationValidator().Validate(profile, ValidPolicy()));

        Assert.Contains("audit.evidenceRetentionDays must be 3650 or less", error.Message, StringComparison.Ordinal);
        Assert.Contains("audit.maximumSelectedVideoScreenshots must be 6 or less", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("synthetic-pat-value!", "TEST_AI_KEY")]
    [InlineData("TEST_ADO_PAT", "sk-synthetic-value!")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnop", "TEST_AI_KEY")]
    public void SEC_001_CredentialReferencesMustBeEnvironmentVariableNamesWithoutEchoingValues(
        string adoReference,
        string aiReference)
    {
        var error = Assert.Throws<ConfigurationValidationException>(() =>
            new DeploymentConfigurationValidator().Validate(
                ValidProfile(adoReference: adoReference, aiReference: aiReference),
                ValidPolicy()));

        Assert.Contains("must be an environment-variable name", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(adoReference, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(aiReference, error.Message, StringComparison.Ordinal);
    }

    private static DeploymentProfileInput ValidProfile(
        string validatedTag = "VALID",
        string incompleteTag = "INCOMPLETE",
        string adoReference = "TEST_ADO_PAT",
        string aiReference = "TEST_AI_KEY",
        int? evidenceRetentionDays = null,
        int? maximumSelectedVideoScreenshots = null) => new()
        {
            Profile = new ConfigurationIdentityInput { Id = " profile-id ", Version = " 1 " },
            IntakePolicy = new PolicyReferenceInput { Path = "intake-policy.yaml", Url = "https://example.invalid/policy" },
            IntakeState = new IntakeStateInput { ValidatedTag = validatedTag, IncompleteTag = incompleteTag },
            Ado = new AzureDevOpsInput
            {
                OrganizationUrl = "https://dev.azure.com/example",
                Project = "Example",
                SavedQueryId = "11111111-1111-1111-1111-111111111111",
                Authentication = new AzureDevOpsAuthenticationInput { PatEnvironmentVariable = adoReference }
            },
            Ai = new AiInput
            {
                Provider = "OpenAI",
                Model = "test-model",
                TimeoutSeconds = 60,
                Authentication = new AiAuthenticationInput { ApiKeyEnvironmentVariable = aiReference },
                Pricing =
            [
                new ModelPricingInput
                {
                    Provider = "OpenAI",
                    Model = "test-model",
                    InputPerMillionTokens = 1,
                    OutputPerMillionTokens = 2,
                    Currency = "usd"
                }
            ]
            },
            Schedule = new ScheduleInput { Enabled = false, Expression = "0 * * * * *", Timezone = "UTC", InitialLookback = "1.00:00:00" },
            Processing = new ProcessingInput
            {
                ExecutionMode = "DRY_RUN",
                Concurrency = 1,
                Retries = 0,
                ContentLimits = new ContentLimitsInput
                {
                    MaximumTotalCharacters = 1_000,
                    MaximumComments = 10,
                    MaximumExtractedTextCharacters = 500
                },
                AttachmentLimits = new AttachmentLimitsInput
                {
                    MaximumCount = 2,
                    MaximumBytesPerAttachment = 1_000,
                    MaximumAggregateBytes = 2_000,
                    MaximumPdfPages = 10
                }
            },
            Audit = new AuditInput
            {
                RetentionDays = 90,
                EvidenceRetentionDays = evidenceRetentionDays,
                MaximumSelectedVideoScreenshots = maximumSelectedVideoScreenshots
            },
            Exclusions =
        [
            new ExclusionInput { Id = "excluded", Field = "Example.State", Operator = "equalsAny", Values = ["Excluded"] }
        ]
        };

    private static IntakePolicyInput ValidPolicy() => new()
    {
        Policy = new ConfigurationIdentityInput { Id = "policy-id", Version = "1" },
        Criteria =
        [
            new CriterionInput
            {
                Id = "criterion",
                DisplayName = "Criterion",
                Description = "Description",
                Applicability = "required",
                Na = new NotApplicableInput { Allowed = false, RequiresExplanation = false },
                EvaluationGuidance = "Guidance"
            }
        ]
    };
}

using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Configuration;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class YamlDeploymentConfigurationLoaderTests
{
    [Fact]
    public void CFG_001_CFG_003_ValidGenericProfileAndPolicyLoad()
    {
        using var fixture = ConfigurationFixture.Create();

        var configuration = new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath);

        Assert.Equal("example", configuration.Profile.Identity.Id);
        Assert.Equal("example-engineering-intake", configuration.Policy.Identity.Id);
        Assert.Single(configuration.Policy.Criteria);
        Assert.StartsWith("sha256:", configuration.PolicyFingerprint, StringComparison.Ordinal);
        Assert.Equal(ExecutionMode.DryRun, configuration.Profile.Processing.ExecutionMode);
        Assert.Equal("INTAKE-VALIDATED", configuration.Profile.IntakeState.ValidatedTag);
        Assert.Equal(60, configuration.Profile.Ai.TimeoutSeconds);
    }

    [Fact]
    public void CFG_001_MalformedProfileYamlFails()
    {
        using var fixture = ConfigurationFixture.Create(profile: "profile: [unterminated");
        Assert.Throws<ConfigurationDocumentException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Fact]
    public void CFG_003_MalformedPolicyYamlFails()
    {
        using var fixture = ConfigurationFixture.Create(policy: "policy: [unterminated");
        Assert.Throws<ConfigurationDocumentException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Fact]
    public void CFG_001_CFG_003_YamlLoaderDelegatesProfileAndPolicySemanticsToSharedValidator()
    {
        using var fixture = ConfigurationFixture.Create();
        var validator = new RecordingValidator();

        new YamlDeploymentConfigurationLoader(validator).Load(fixture.ProfilePath);

        Assert.Equal(1, validator.ProfileCalls);
        Assert.Equal(1, validator.PolicyCalls);
    }

    [Theory]
    [InlineData("  id: example\n", "  id: \n")]
    [InlineData("  path: intake-policy.yaml\n", "  path: \n")]
    [InlineData("  url: https://example.invalid/standard\n", "  url: \n")]
    [InlineData("  url: https://example.invalid/standard\n", "  url: not-a-url\n")]
    [InlineData("  savedQueryId: 11111111-1111-1111-1111-111111111111\n", "  savedQueryId: \n")]
    [InlineData("  provider: openai\n", "  provider: unsupported\n")]
    [InlineData("  model: example-model\n", "  model: \n")]
    [InlineData("  timeoutSeconds: 60\n", "  timeoutSeconds: 0\n")]
    [InlineData("  executionMode: DRY_RUN\n", "  executionMode: SOMETIMES\n")]
    [InlineData("  validatedTag: INTAKE-VALIDATED\n", "  validatedTag: \n")]
    [InlineData("  concurrency: 2\n", "  concurrency: 0\n")]
    [InlineData("  retries: 2\n", "  retries: -1\n")]
    [InlineData("    maximumTotalCharacters: 1000\n", "    maximumTotalCharacters: 0\n")]
    [InlineData("    maximumCount: 2\n", "    maximumCount: 0\n")]
    [InlineData("  retentionDays: 90\n", "  retentionDays: 0\n")]
    public void CFG_001_InvalidProfilePropertyFails(string original, string replacement)
    {
        using var fixture = ConfigurationFixture.Create(profile: ConfigurationFixture.ValidProfile.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<ConfigurationValidationException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Fact]
    public void CFG_001_IntakeTagsMustBeDistinct()
    {
        using var fixture = ConfigurationFixture.Create(profile: ConfigurationFixture.ValidProfile.Replace(
            "incompleteTag: INTAKE-INCOMPLETE", "incompleteTag: INTAKE-VALIDATED", StringComparison.Ordinal));
        Assert.Throws<ConfigurationValidationException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Fact]
    public void NFR_008_OptionalPricingIsDataDrivenAndValidated()
    {
        var profile = ConfigurationFixture.ValidProfile.Replace("  pricing: []", """
              pricing:
                - provider: openai
                  model: synthetic-model
                  inputPerMillionTokens: 1.25
                  outputPerMillionTokens: 9.50
                  currency: USD
                  identity: synthetic-v1
            """, StringComparison.Ordinal);
        using var fixture = ConfigurationFixture.Create(profile: profile);
        var pricing = new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath).Profile.Ai.Pricing.Single();
        Assert.Equal(1.25m, pricing.InputPerMillionTokens);
        Assert.Equal(9.50m, pricing.OutputPerMillionTokens);
        Assert.Equal("synthetic-v1", pricing.Identity);
    }

    [Fact]
    public void CFG_003_DuplicateCriterionIdsFail()
    {
        var duplicate = ConfigurationFixture.ValidPolicy + """

              - id: problem_statement
                displayName: Duplicate Problem Statement
                description: A duplicate stable identifier.
                applicability: contextual
                na:
                  allowed: true
                  requiresExplanation: true
                evaluationGuidance: This duplicate must be rejected.
            """;
        using var fixture = ConfigurationFixture.Create(policy: duplicate);
        Assert.Throws<ConfigurationValidationException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Theory]
    [InlineData("    applicability: required\n", "    applicability: sometimes\n")]
    [InlineData("      allowed: false\n      requiresExplanation: false\n", "      allowed: false\n      requiresExplanation: true\n")]
    [InlineData("      allowed: false\n", "")]
    public void CFG_003_InvalidPolicySemanticsFail(string original, string replacement)
    {
        using var fixture = ConfigurationFixture.Create(policy: ConfigurationFixture.ValidPolicy.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<ConfigurationValidationException>(() => new YamlDeploymentConfigurationLoader().Load(fixture.ProfilePath));
    }

    [Fact]
    public void AC_19_DifferentProfileAndPolicyLoadWithoutCodeChanges()
    {
        using var first = ConfigurationFixture.Create();
        using var second = ConfigurationFixture.Create(
            profile: ConfigurationFixture.ValidProfile.Replace("id: example", "id: alternate", StringComparison.Ordinal),
            policy: ConfigurationFixture.ValidPolicy.Replace("id: example-engineering-intake", "id: alternate-policy", StringComparison.Ordinal));
        var loader = new YamlDeploymentConfigurationLoader();

        var profileA = loader.Load(first.ProfilePath);
        var profileB = loader.Load(second.ProfilePath);

        Assert.Equal("example", profileA.Profile.Identity.Id);
        Assert.Equal("alternate", profileB.Profile.Identity.Id);
        Assert.NotEqual(profileA.PolicyFingerprint, profileB.PolicyFingerprint);
    }

    [Fact]
    public void AUD_005_FormattingOnlyPolicyChangesDoNotChangeFingerprint()
    {
        using var first = ConfigurationFixture.Create();
        using var formatted = ConfigurationFixture.Create(policy: "# formatting comment\n\n" + ConfigurationFixture.ValidPolicy.Replace("version: \"1\"", "version: '1'", StringComparison.Ordinal));
        var loader = new YamlDeploymentConfigurationLoader();

        Assert.Equal(loader.Load(first.ProfilePath).PolicyFingerprint, loader.Load(formatted.ProfilePath).PolicyFingerprint);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        public const string ValidProfile = """
            profile:
              id: example
              version: "1"
            intakePolicy:
              path: intake-policy.yaml
              url: https://example.invalid/standard
            intakeState:
              validatedTag: INTAKE-VALIDATED
              incompleteTag: INTAKE-INCOMPLETE
            ado:
              organizationUrl: https://dev.azure.com/example
              project: Example
              savedQueryId: 11111111-1111-1111-1111-111111111111
              authentication:
                patEnvironmentVariable: TEST_ADO_PAT
            ai:
              provider: openai
              model: example-model
              timeoutSeconds: 60
              authentication:
                apiKeyEnvironmentVariable: TEST_AI_KEY
              pricing: []
            schedule:
              enabled: false
              expression: "0 * * * * *"
              timezone: UTC
              initialLookback: 1.00:00:00
            processing:
              executionMode: DRY_RUN
              concurrency: 2
              retries: 2
              contentLimits:
                maximumTotalCharacters: 1000
                maximumComments: 10
                maximumExtractedTextCharacters: 500
              attachmentLimits:
                maximumCount: 2
                maximumBytesPerAttachment: 1000
                maximumAggregateBytes: 2000
                maximumPdfPages: 10
            audit:
              retentionDays: 90
            exclusions:
              - id: excluded-state
                field: Example.State
                operator: equalsAny
                values: [Excluded]
            """;

        public const string ValidPolicy = """
            policy:
              id: example-engineering-intake
              version: "1"
            criteria:
              - id: problem_statement
                displayName: Problem Statement
                description: A clear statement.
                applicability: required
                na:
                  allowed: false
                  requiresExplanation: false
                evaluationGuidance: Determine whether the statement is clear.
            """;

        private ConfigurationFixture(string directory)
        {
            Directory = directory;
            ProfilePath = Path.Combine(directory, "profile.yaml");
        }

        public string Directory { get; }
        public string ProfilePath { get; }

        public static ConfigurationFixture Create(string? profile = null, string? policy = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "profile.yaml"), profile ?? ValidProfile);
            File.WriteAllText(Path.Combine(directory, "intake-policy.yaml"), policy ?? ValidPolicy);
            return new ConfigurationFixture(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class RecordingValidator : IDeploymentConfigurationValidator
    {
        public int ProfileCalls { get; private set; }
        public int PolicyCalls { get; private set; }

        public DeploymentConfiguration Validate(DeploymentProfileInput profileInput, IntakePolicyInput policyInput,
            string profileSource = "profile", string policySource = "intake policy") =>
            throw new NotSupportedException();

        public DeploymentProfile ValidateProfile(DeploymentProfileInput input, string source = "profile")
        {
            ProfileCalls++;
            return new DeploymentProfile(
                new ProfileIdentity("profile", "1"),
                new IntakePolicyReference("intake-policy.yaml", new Uri("https://example.invalid/policy")),
                new IntakeStateConfiguration("VALID", "INCOMPLETE"),
                new AzureDevOpsConfiguration(new Uri("https://dev.azure.com/example"), "Example",
                    Guid.Parse("11111111-1111-1111-1111-111111111111"), new CredentialReference("TEST_ADO_PAT")),
                new AiConfiguration("openai", "model", new CredentialReference("TEST_AI_KEY")),
                new ScheduleConfiguration(false, string.Empty, "UTC", TimeSpan.FromDays(1)),
                new ProcessingConfiguration(ExecutionMode.DryRun, 1, 0,
                    new ContentLimits(1000, 10, 500), new AttachmentLimits(2, 1000, 2000, 10)),
                new AuditConfiguration(90), []);
        }

        public IntakePolicy ValidatePolicy(IntakePolicyInput input, string source = "intake policy")
        {
            PolicyCalls++;
            return new IntakePolicy(new PolicyIdentity("policy", "1"),
                [new IntakeCriterion("criterion", "Criterion", "Description", CriterionApplicability.Required,
                    new NotApplicablePolicy(false, false), "Guidance")]);
        }
    }
}

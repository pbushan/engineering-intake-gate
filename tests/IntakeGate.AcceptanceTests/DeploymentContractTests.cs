using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class DeploymentContractTests
{
    [Fact]
    public void DEP_001_TrackedTestComposeDefinesOneApplicationPlusPurposeBuiltMockAdoAndPersistentVolume()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "test-harness", "compose", "docker-compose.test.yml"));

        Assert.Contains("  intake-gate:", compose, StringComparison.Ordinal);
        Assert.Contains("  mock-ado:", compose, StringComparison.Ordinal);
        Assert.Contains("intake-gate-test-data:/app/data", compose, StringComparison.Ordinal);
        Assert.Contains("volumes:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("intake-worker", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intake-api", compose, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, compose.Split("  intake-gate:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CFG_008_AC_19_ComposeTreatsMountedYamlAsExplicitImportInputAndReusesImage()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "test-harness", "compose", "docker-compose.test.yml"));
        var harness = File.ReadAllText(Path.Combine(root, "test-harness", "test.sh"));

        Assert.Contains("INTAKE_GATE_PROFILE_DIRECTORY", compose, StringComparison.Ordinal);
        Assert.Contains(":/app/config:ro", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile__ConfigurationPath", compose, StringComparison.Ordinal);
        Assert.Contains("--import-legacy-profile /app/config/profile.yaml", harness, StringComparison.Ordinal);
        Assert.Contains("--no-build", harness, StringComparison.Ordinal);
        Assert.Contains("profiles/alternate", harness, StringComparison.Ordinal);
    }

    [Fact]
    public void DEP_004_ProductComposeStartsOneBackendAndOneStatelessUiWithoutMockOrImplicitProfileSource()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        var serviceLines = File.ReadAllLines(Path.Combine(root, "docker-compose.yml"));
        Assert.Equal(1, serviceLines.Count(line => line == "  intake-gate:"));
        Assert.Equal(1, serviceLines.Count(line => line == "  intake-gate-ui:"));
        Assert.DoesNotContain("mock-ado", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Profile__", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("/app/config", compose, StringComparison.Ordinal);
        Assert.Contains("intake-gate-product-data:/app/data", compose, StringComparison.Ordinal);
        Assert.Contains("Authentication__DataProtectionKeyPath: /app/data/data-protection-keys", compose, StringComparison.Ordinal);
        Assert.Contains("context: ./src/IntakeGate.Web", compose, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnyOrigin", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void AUTH_009_ProductComposePersistsSessionKeysSeparatelyFromCredentialEncryptionMaterial()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "IntakeGate.Host", "Program.cs"));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        Assert.Contains("PersistKeysToFileSystem", program, StringComparison.Ordinal);
        Assert.Contains("SetApplicationName(\"EngineeringIntakeGate\")", program, StringComparison.Ordinal);
        Assert.Contains("/app/data/data-protection-keys", compose, StringComparison.Ordinal);
        Assert.Contains("/app/data/intake-gate.db", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretStore__KeyPath: /app/data/data-protection-keys", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void CFG_002_LocalEnvironmentFilesAreIgnoredButExampleIsTracked()
    {
        var root = FindRepositoryRoot();
        var ignore = File.ReadAllLines(Path.Combine(root, ".gitignore"));
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));

        Assert.Contains(".env", ignore);
        Assert.Contains("!.env.example", ignore);
        Assert.DoesNotContain("password=", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", example, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intake-gate.secret-key", ignore);
        Assert.Contains("docker-compose.override.yml", ignore);
        Assert.Contains("docker-compose.*.local.yml", ignore);
        Assert.DoesNotContain("docker-compose.yml", ignore);
    }

    [Fact]
    public void AC_18_RealAiComposeIsAnOverrideOfTheSameSingleApplicationAndUsesExternalSecrets()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "test-harness", "compose", "docker-compose.real-ai.yml"));
        var script = File.ReadAllText(Path.Combine(root, "test-harness", "test-real-ai.sh"));

        Assert.Equal(1, compose.Split("  intake-gate:", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("build:", compose, StringComparison.Ordinal);
        Assert.Contains("AiRuntime__Provider", compose, StringComparison.Ordinal);
        Assert.Contains("AiRuntime__Model", compose, StringComparison.Ordinal);
        Assert.Contains("REAL_AI_API_KEY", compose, StringComparison.Ordinal);
        Assert.Contains("docker-compose.test.yml -f test-harness/compose/docker-compose.real-ai.yml", script, StringComparison.Ordinal);
        Assert.Contains("dryRun", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", compose, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EngineeringIntakeGate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

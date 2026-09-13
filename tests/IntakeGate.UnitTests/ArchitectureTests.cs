using System.Xml.Linq;
using IntakeGate.Application.Persistence;
using IntakeGate.Application.Secrets;
using IntakeGate.Application.WorkItems;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ARCH_002_ProductionSourceHasNoTeamSpecificCoupling()
    {
        var root = RepositoryRoot.Find();
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        var offenders = sourceFiles
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("profiles/example", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("profiles/alternate", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ARCH_003_SolutionHasExactlyOneExecutableProject()
    {
        var root = RepositoryRoot.Find();
        var executableProjects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(IsExecutableProject)
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Equal([Path.Combine("src", "IntakeGate.Host", "IntakeGate.Host.csproj")], executableProjects);
    }

    [Fact]
    public void ARCH_005_DomainAndApplicationHaveOnlyAllowedDependencies()
    {
        var root = RepositoryRoot.Find();
        var domainProject = XDocument.Load(Path.Combine(root, "src", "IntakeGate.Domain", "IntakeGate.Domain.csproj"));
        var applicationProject = XDocument.Load(Path.Combine(root, "src", "IntakeGate.Application", "IntakeGate.Application.csproj"));

        Assert.Empty(domainProject.Descendants("ProjectReference"));
        Assert.Empty(domainProject.Descendants("PackageReference"));
        Assert.Empty(applicationProject.Descendants("PackageReference"));

        var references = applicationProject.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["../IntakeGate.Domain/IntakeGate.Domain.csproj"], references);
    }

    [Fact]
    public void DB_001_CFG_007_OnlyCentralMigratorOwnsProductionSchemaEvolution()
    {
        var root = RepositoryRoot.Find();
        var sourceRoot = Path.Combine(root, "src");
        var schemaOwners = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("CREATE TABLE", StringComparison.Ordinal) ||
                       text.Contains("ALTER TABLE", StringComparison.Ordinal) ||
                       text.Contains("PRAGMA user_version", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Equal(
            [Path.Combine("src", "IntakeGate.Infrastructure", "Persistence", "SqliteDatabaseMigrator.cs")],
            schemaOwners);
    }

    [Fact]
    public void CFG_007_CFG_008_NormalStartupHasNoYamlAuthorityHeuristicOrMutableProfileWorkflow()
    {
        var root = RepositoryRoot.Find();
        var program = File.ReadAllText(Path.Combine(root, "src", "IntakeGate.Host", "Program.cs"));
        var appsettings = File.ReadAllText(Path.Combine(root, "src", "IntakeGate.Host", "appsettings.json"));
        var repositoryMethods = typeof(ISingletonProfileRepository).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain("YamlDeploymentConfigurationLoader", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationPath", appsettings, StringComparison.Ordinal);
        Assert.DoesNotContain("profiles/example", program, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profiles/alternate", program, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["CreateAsync", "ExistsAsync", "LoadAsync"], repositoryMethods);
    }

    [Fact]
    public void ARCH_002_NFR_006_Phase3IntroducesNoProviderOrAiSdkDependency()
    {
        var root = RepositoryRoot.Find();
        var projectText = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("Azure.DevOps", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenAI", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anthropic", projectText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADO_001_Phase8WorkItemProviderExposesReadOperationsOnly()
    {
        var methods = typeof(IWorkItemSource).GetMethods();

        Assert.Equal(["ExecuteSavedQueryAsync", "GetWorkItemAsync"],
            methods.Select(method => method.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(methods, method => Assert.DoesNotMatch("Update|Patch|Create|Delete|Comment|Tag|State|Assign|Write", method.Name));
    }

    [Fact]
    public void ADO_001_Phase8ContainsNoAzureDevOpsSdkAndNoWriteProviderContract()
    {
        var root = RepositoryRoot.Find();
        var projects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(File.ReadAllText);
        Assert.DoesNotContain(projects, text => text.Contains("Azure.DevOps", StringComparison.OrdinalIgnoreCase));

        var applicationSource = Directory.EnumerateFiles(Path.Combine(root, "src", "IntakeGate.Application"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText);
        Assert.DoesNotContain(applicationSource, text => text.Contains("Microsoft.TeamFoundation", StringComparison.Ordinal));
    }

    [Fact]
    public void PHASE9_AI_002_WriterIsNarrowAndSeparateFromAiContracts()
    {
        var methods = typeof(IWorkItemWriter).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["AddValidatorCommentAsync", "UpdateIntakeTagsAsync"], methods);

        var root = RepositoryRoot.Find();
        var aiSource = Directory.EnumerateFiles(Path.Combine(root, "src", "IntakeGate.Infrastructure", "Ai"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText);
        Assert.DoesNotContain(aiSource, text => text.Contains("IWorkItemWriter", StringComparison.Ordinal));
    }

    [Fact]
    public void SEC_010_SecretBoundaryHasExplicitSlotsAndNoSerializableRawValueProperty()
    {
        Assert.Equal([CredentialSlot.AzureDevOps, CredentialSlot.OpenAi, CredentialSlot.Anthropic],
            Enum.GetValues<CredentialSlot>());
        Assert.Empty(typeof(SecretValue).GetProperties());
        Assert.DoesNotContain(typeof(CredentialMetadata).GetProperties(), property =>
            property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Cipher", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("EnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExecutableProject(string path)
    {
        var document = XDocument.Load(path);
        var sdk = document.Root?.Attribute("Sdk")?.Value;
        var outputType = document.Descendants("OutputType").Select(element => element.Value).FirstOrDefault();
        return string.Equals(sdk, "Microsoft.NET.Sdk.Web", StringComparison.Ordinal) ||
               string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class RepositoryRoot
{
    public static string Find()
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

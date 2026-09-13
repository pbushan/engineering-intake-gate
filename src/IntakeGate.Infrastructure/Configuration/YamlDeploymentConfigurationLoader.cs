using IntakeGate.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IntakeGate.Infrastructure.Configuration;

/// <summary>YAML/file adapter; semantic rules belong only to DeploymentConfigurationValidator.</summary>
public sealed class YamlDeploymentConfigurationLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private readonly IDeploymentConfigurationValidator validator;

    public YamlDeploymentConfigurationLoader(IDeploymentConfigurationValidator? validator = null)
    {
        this.validator = validator ?? new DeploymentConfigurationValidator();
    }

    public DeploymentConfiguration Load(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            throw new ConfigurationValidationException(
                "An exact legacy profile YAML path is required for explicit import.");
        }

        var fullProfilePath = Path.GetFullPath(profilePath);
        var profileInput = Deserialize<DeploymentProfileInput>(fullProfilePath, "profile");
        var profile = validator.ValidateProfile(profileInput, fullProfilePath);
        var policyPath = Path.GetFullPath(profile.IntakePolicy.Path, Path.GetDirectoryName(fullProfilePath)!);
        var policyInput = Deserialize<IntakePolicyInput>(policyPath, "intake policy");
        var policy = validator.ValidatePolicy(policyInput);
        return new DeploymentConfiguration(profile, policy, PolicyFingerprint.Create(policy));
    }

    /// <summary>
    /// Parses an explicitly supplied, bounded profile/policy pair without resolving or reading a
    /// browser-controlled filesystem path. The profile's legacy policy path is retained as
    /// metadata only; the supplied policy content is the sole policy input.
    /// </summary>
    public DeploymentConfiguration LoadContents(string profileYaml, string policyYaml)
    {
        ArgumentNullException.ThrowIfNull(profileYaml);
        ArgumentNullException.ThrowIfNull(policyYaml);
        var profileInput = DeserializeContent<DeploymentProfileInput>(profileYaml, "profile");
        var profile = validator.ValidateProfile(profileInput, "uploaded legacy profile");
        var policyInput = DeserializeContent<IntakePolicyInput>(policyYaml, "intake policy");
        var policy = validator.ValidatePolicy(policyInput, "uploaded legacy intake policy");
        return new DeploymentConfiguration(profile, policy, PolicyFingerprint.Create(policy));
    }

    private static T Deserialize<T>(string path, string documentName)
    {
        try
        {
            if (!File.Exists(path))
                throw new ConfigurationDocumentException($"The configured {documentName} file does not exist: {path}");

            using var reader = File.OpenText(path);
            return Deserializer.Deserialize<T>(reader)
                ?? throw new ConfigurationDocumentException($"The configured {documentName} file is empty: {path}");
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new ConfigurationDocumentException(
                $"The configured {documentName} YAML is malformed near line {exception.Start.Line}.", exception);
        }
        catch (IOException exception)
        {
            throw new ConfigurationDocumentException(
                $"The configured {documentName} file could not be read: {path}", exception);
        }
    }

    private static T DeserializeContent<T>(string content, string documentName)
    {
        try
        {
            using var reader = new StringReader(content);
            return Deserializer.Deserialize<T>(reader)
                ?? throw new ConfigurationDocumentException($"The supplied {documentName} YAML is empty.");
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new ConfigurationDocumentException(
                $"The supplied {documentName} YAML is malformed near line {exception.Start.Line}.", exception);
        }
    }
}

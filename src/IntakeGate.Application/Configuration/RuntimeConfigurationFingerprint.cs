using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntakeGate.Application.Configuration;

public static class RuntimeConfigurationFingerprint
{
    public static string Create(
        DeploymentConfiguration configuration,
        DateTimeOffset azureDevOpsCredentialRevision,
        DateTimeOffset aiCredentialRevision)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            profile = DeploymentConfigurationInputs.Profile(configuration.Profile),
            policy = DeploymentConfigurationInputs.Policy(configuration.Policy),
            policyFingerprint = configuration.PolicyFingerprint,
            azureDevOpsCredentialRevision = azureDevOpsCredentialRevision.ToUniversalTime().ToString("O"),
            aiCredentialRevision = aiCredentialRevision.ToUniversalTime().ToString("O")
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

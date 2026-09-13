using System.Security.Cryptography;
using System.Text;
using IntakeGate.Application.Configuration;

namespace IntakeGate.Application.AzureDevOps;

public static class AzureDevOpsConfigurationFingerprint
{
    public static string Create(string profileId, AzureDevOpsConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(configuration);
        var canonical = string.Join('\n',
            profileId.Trim(),
            NormalizeOrganizationUrl(configuration.OrganizationUrl).AbsoluteUri,
            configuration.Project.Trim(),
            configuration.SavedQueryId.ToString("D"));
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    public static Uri NormalizeOrganizationUrl(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new UriBuilder(value)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = value.AbsolutePath.TrimEnd('/')
        };
        return builder.Uri;
    }
}

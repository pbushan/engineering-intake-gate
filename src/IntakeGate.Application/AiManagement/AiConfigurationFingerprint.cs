using System.Security.Cryptography;
using System.Text;

namespace IntakeGate.Application.AiManagement;

public static class AiConfigurationFingerprint
{
    public static string Create(bool profileConfigured, string? profileId, string? provider, string? model)
    {
        var canonical = string.Join('\n', profileConfigured ? "profile" : "profileless",
            profileId ?? string.Empty, provider ?? string.Empty, model ?? string.Empty);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

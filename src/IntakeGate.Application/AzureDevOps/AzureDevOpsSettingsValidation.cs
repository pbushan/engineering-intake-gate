namespace IntakeGate.Application.AzureDevOps;

public static class AzureDevOpsSettingsValidation
{
    public static bool TryNormalizeOrganization(string? value, out Uri? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        normalized = AzureDevOpsConfigurationFingerprint.NormalizeOrganizationUrl(parsed);
        return true;
    }

    public static bool TryNormalizeProject(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0;
    }
}

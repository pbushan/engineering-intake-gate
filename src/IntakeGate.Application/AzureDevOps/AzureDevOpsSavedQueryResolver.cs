namespace IntakeGate.Application.AzureDevOps;

public static class AzureDevOpsSavedQueryResolver
{
    public static bool TryResolve(
        string? input,
        Uri configuredOrganizationUrl,
        string configuredProject,
        out Guid queryId)
    {
        queryId = Guid.Empty;
        var candidate = input?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (Guid.TryParse(candidate, out queryId) && queryId != Guid.Empty) return true;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)) return false;

        var organization = AzureDevOpsConfigurationFingerprint.NormalizeOrganizationUrl(configuredOrganizationUrl);
        if (!string.Equals(url.Scheme, organization.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(url.Host, organization.Host, StringComparison.OrdinalIgnoreCase) ||
            url.Port != organization.Port) return false;

        var organizationSegments = Segments(organization.AbsolutePath);
        var urlSegments = Segments(url.AbsolutePath);
        if (urlSegments.Count < organizationSegments.Count + 4) return false;
        for (var index = 0; index < organizationSegments.Count; index++)
            if (!string.Equals(urlSegments[index], organizationSegments[index], StringComparison.OrdinalIgnoreCase)) return false;

        var offset = organizationSegments.Count;
        if (!string.Equals(urlSegments[offset], configuredProject.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(urlSegments[offset + 1], "_queries", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(urlSegments[offset + 2], "query", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(urlSegments[offset + 3], out queryId) || queryId == Guid.Empty) return false;
        return true;
    }

    private static IReadOnlyList<string> Segments(string path) => path
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();
}

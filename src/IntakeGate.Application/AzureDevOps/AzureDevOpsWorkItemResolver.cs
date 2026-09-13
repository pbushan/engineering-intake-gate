namespace IntakeGate.Application.AzureDevOps;

/// <summary>Canonicalizes only work-item identities inside the configured ADO boundary.</summary>
public static class AzureDevOpsWorkItemResolver
{
    public static bool TryResolve(
        string? input,
        Uri configuredOrganizationUrl,
        string configuredProject,
        out int workItemId)
    {
        workItemId = 0;
        var candidate = input?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (int.TryParse(candidate, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0)
            return true;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)) return false;

        var organization = AzureDevOpsConfigurationFingerprint.NormalizeOrganizationUrl(configuredOrganizationUrl);
        if (!string.Equals(url.Scheme, organization.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(url.Host, organization.Host, StringComparison.OrdinalIgnoreCase) ||
            url.Port != organization.Port) return false;

        var organizationSegments = Segments(organization.AbsolutePath);
        var urlSegments = Segments(url.AbsolutePath);
        if (urlSegments.Count != organizationSegments.Count + 4) return false;
        for (var index = 0; index < organizationSegments.Count; index++)
            if (!string.Equals(urlSegments[index], organizationSegments[index], StringComparison.OrdinalIgnoreCase))
                return false;

        var offset = organizationSegments.Count;
        return string.Equals(urlSegments[offset], configuredProject.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(urlSegments[offset + 1], "_workitems", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(urlSegments[offset + 2], "edit", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(urlSegments[offset + 3], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0;
    }

    public static Uri CreateCanonicalUrl(Uri configuredOrganizationUrl, string configuredProject, int workItemId)
    {
        if (workItemId <= 0) throw new ArgumentOutOfRangeException(nameof(workItemId));
        var organization = AzureDevOpsConfigurationFingerprint.NormalizeOrganizationUrl(configuredOrganizationUrl);
        var escapedProject = Uri.EscapeDataString(configuredProject.Trim());
        return new Uri(organization.AbsoluteUri.TrimEnd('/') +
                       $"/{escapedProject}/_workitems/edit/{workItemId}", UriKind.Absolute);
    }

    private static IReadOnlyList<string> Segments(string path) => path
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();
}

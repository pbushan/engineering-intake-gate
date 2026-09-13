using System.Net.Http.Headers;
using System.Text;

namespace IntakeGate.Infrastructure.AzureDevOps;

internal static class AzureDevOpsHttp
{
    public static Uri BuildProjectApiBase(Uri organizationUrl, string project)
    {
        var organization = organizationUrl.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(new Uri(organization), Uri.EscapeDataString(project) + "/");
    }

    public static HttpRequestMessage CreateGet(Uri projectApiBase, string relativePath, string pat)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(projectApiBase, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

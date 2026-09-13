using System.Net.Http.Json;
using System.Text.Json;

namespace IntakeGate.AcceptanceTests;

internal static class AuthenticatedTestClient
{
    public const string AdminPassword = "deterministic-admin-password";

    public static async Task AuthenticateAdminAsync(HttpClient client)
    {
        await RefreshCsrfTokenAsync(client);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/bootstrap/status");
        var endpoint = status.GetProperty("available").GetBoolean()
            ? "/api/auth/bootstrap"
            : "/api/auth/login";
        using var response = status.GetProperty("available").GetBoolean()
            ? await client.PostAsJsonAsync(endpoint, new
            {
                username = "acceptance-admin",
                displayName = "Acceptance Admin",
                password = AdminPassword
            })
            : await client.PostAsJsonAsync(endpoint, new
            {
                username = "acceptance-admin",
                password = AdminPassword
            });
        response.EnsureSuccessStatusCode();
        await RefreshCsrfTokenAsync(client);
    }

    public static async Task RefreshCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", response.GetProperty("token").GetString());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class LocalAuthenticationEndpointTests
{
    private const string InitialPassword = "correct-horse-phase2a";

    [Fact]
    public async Task AUTH_001_AUTH_003_FreshBootstrapHashesPasswordClosesBootstrapAndCreatesSafeSession()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/auth/bootstrap/status");
        Assert.True(before.GetProperty("available").GetBoolean());
        var csrfToken = await SetCsrfAsync(client);

        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = " First.Admin ",
            displayName = "First Admin",
            password = InitialPassword
        });

        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        var body = await bootstrap.Content.ReadAsStringAsync();
        Assert.DoesNotContain(InitialPassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
        var cookies = bootstrap.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, value => value.StartsWith("IntakeGate.Authentication=", StringComparison.Ordinal));
        Assert.Contains(cookies, value => value.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, value => value.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        var authenticationCookie = cookies
            .Single(value => value.StartsWith("IntakeGate.Authentication=", StringComparison.Ordinal))
            .Split(';', 2)[0]
            .Split('=', 2)[1];

        var after = await client.GetFromJsonAsync<JsonElement>("/api/auth/bootstrap/status");
        Assert.False(after.GetProperty("available").GetBoolean());
        await SetCsrfAsync(client);
        using var second = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "second-admin",
            password = "another-safe-password"
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var session = await client.GetAsync("/api/auth/session");
        session.EnsureSuccessStatusCode();
        var sessionBody = await session.Content.ReadAsStringAsync();
        Assert.Contains("First.Admin", sessionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", sessionBody, StringComparison.OrdinalIgnoreCase);

        var storedHash = await ScalarAsync<string>(fixture.DatabasePath, "SELECT password_hash FROM local_users;");
        Assert.NotEqual(InitialPassword, storedHash);
        var databaseText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.DoesNotContain(InitialPassword, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(csrfToken, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(authenticationCookie, databaseText, StringComparison.Ordinal);
        Assert.Equal("BootstrapAdmin", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT operation FROM control_plane_audits;"));
        Assert.Equal("First.Admin", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT actor_username FROM control_plane_audits;"));
    }

    [Fact]
    public async Task AUTH_001_ConcurrentHttpBootstrapAllowsExactlyOneInitialAdmin()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        await SetCsrfAsync(first);
        await SetCsrfAsync(second);

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/auth/bootstrap", new
            { username = "race-one", password = "race-one-safe-password" }),
            second.PostAsJsonAsync("/api/auth/bootstrap", new
            { username = "race-two", password = "race-two-safe-password" }));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(1L, await ScalarAsync<long>(fixture.DatabasePath, "SELECT COUNT(*) FROM local_users;"));
            Assert.Equal("Admin", await ScalarAsync<string>(fixture.DatabasePath, "SELECT role FROM local_users;"));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task AUTH_004_LoginLogoutAndFailureResponsesAreSafe()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using (var bootstrapClient = fixture.CreateClient())
            await BootstrapAsync(bootstrapClient, "admin", InitialPassword);

        using var client = fixture.CreateClient();
        await SetCsrfAsync(client);
        using var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "missing", password = "wrong-password-value" });
        using var wrong = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "wrong-password-value" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());

        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "ADMIN", password = InitialPassword });
        login.EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var session = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task AUTH_005_AUTH_006_AdminCreatesViewerAndViewerIsServerSideReadOnly()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using var admin = fixture.CreateClient();
        await BootstrapAsync(admin, "admin", InitialPassword);
        await SetCsrfAsync(admin);
        using var created = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "viewer",
            displayName = "Read Only",
            password = "viewer-safe-password",
            role = "viewer"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain("password", await created.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var list = await admin.GetAsync("/api/users");
        list.EnsureSuccessStatusCode();
        Assert.DoesNotContain("password", await list.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("admin", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT actor_username FROM control_plane_audits WHERE operation = 'UserCreated';"));

        using var viewer = fixture.CreateClient();
        await LoginAsync(viewer, "viewer", "viewer-safe-password");
        using var version = await viewer.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        var versionBody = await version.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Engineering Intake Gate", versionBody.GetProperty("application").GetString());
        Assert.Equal("2026.9.2", versionBody.GetProperty("version").GetString());
        using var profile = await viewer.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var run = await viewer.PostAsync("/api/runs", null);
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
        using var create = await viewer.PostAsJsonAsync("/api/users", new
        { username = "forbidden", password = "forbidden-safe-password", role = "viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task AUTH_007_OwnPasswordChangeRequiresCurrentPasswordAndInvalidatesSession()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using var client = fixture.CreateClient();
        await BootstrapAsync(client, "admin", InitialPassword);
        await SetCsrfAsync(client);
        using var wrong = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "incorrect-current", newPassword = "replacement-safe-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        using var changed = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = InitialPassword, newPassword = "replacement-safe-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);

        using var oldPassword = fixture.CreateClient();
        await SetCsrfAsync(oldPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldPassword.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = InitialPassword })).StatusCode);
        using var newPassword = fixture.CreateClient();
        await LoginAsync(newPassword, "admin", "replacement-safe-password");
        Assert.Equal(HttpStatusCode.OK, (await newPassword.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal("admin", await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT actor_username FROM control_plane_audits WHERE operation = 'PasswordChanged';"));
        var auditText = await ScalarAsync<string>(fixture.DatabasePath,
            "SELECT group_concat(changed_fields_json, ',') FROM control_plane_audits;");
        Assert.DoesNotContain(InitialPassword, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-safe-password", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUTH_008_StateChangingRequestsRequireCsrfWhileReadsRemainUsable()
    {
        await using var fixture = await AuthFixture.CreateAsync();
        using var client = fixture.CreateClient();
        await BootstrapAsync(client, "admin", InitialPassword);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        using var missing = await client.PostAsJsonAsync("/api/users", new
        { username = "blocked", password = "blocked-safe-password", role = "viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);

        await SetCsrfAsync(client);
        using var accepted = await client.PostAsJsonAsync("/api/users", new
        { username = "allowed", password = "allowed-safe-password", role = "viewer" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task BootstrapAsync(HttpClient client, string username, string password)
    {
        await SetCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username, password });
        response.EnsureSuccessStatusCode();
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        await SetCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
    }

    private static async Task<string> SetCsrfAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var token = body.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        return token;
    }

    private static async Task<T> ScalarAsync<T>(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class AuthFixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string? previousOverride;
        private readonly WebApplicationFactory<Program> factory;

        private AuthFixture(string databasePath, string overridePath, string? previousOverride,
            WebApplicationFactory<Program> factory)
        {
            DatabasePath = databasePath;
            this.overridePath = overridePath;
            this.previousOverride = previousOverride;
            this.factory = factory;
        }

        public string DatabasePath { get; }
        public HttpClient CreateClient() => factory.CreateClient();

        public static async Task<AuthFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-auth-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-auth-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            }));
            var previous = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Test"));
            return new AuthFixture(databasePath, overridePath, previous, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            SqliteConnection.ClearAllPools();
            File.Delete(overridePath);
            File.Delete(DatabasePath);
            File.Delete(DatabasePath + "-shm");
            File.Delete(DatabasePath + "-wal");
        }
    }
}

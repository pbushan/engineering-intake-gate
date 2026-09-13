using IntakeGate.Host.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class ProductApiSecurityMatrixTests
{
    [Theory]
    [InlineData("AiRuntime:Model", "out-of-band-model")]
    [InlineData("AdoRuntime:Project", "Out-of-band project")]
    public async Task CFG_007_Phase7_ProductModeRejectsOutOfBandRuntimeProfileAuthority(string key, string value)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("OperationalDatabase:Path", Path.Combine(directory, "authority.db"));
                builder.UseSetting(key, value);
            });

            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var client = factory.CreateClient();
                using var response = await client.GetAsync("/health/live");
            });
            Assert.Contains("Product runtime authority is SQLite", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AUTH_005_AUTH_008_Phase7_ProductRouteMetadataEnforcesRbacAndCsrfMatrix()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"intake-gate-route-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("OperationalDatabase:Path", Path.Combine(directory, "matrix.db"));
            });
            using var client = factory.CreateClient();
            using var startup = await client.GetAsync("/health/live");
            startup.EnsureSuccessStatusCode();

            var routes = factory.Services.GetServices<EndpointDataSource>()
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => new ProductRoute(
                    "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                    endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                    endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                    endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                    endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true))
                .Where(route => route.Pattern.StartsWith("/api/", StringComparison.Ordinal) ||
                                route.Pattern.StartsWith("/health/", StringComparison.Ordinal) ||
                                route.Pattern.StartsWith("/openapi/", StringComparison.Ordinal))
                .ToArray();

            Assert.NotEmpty(routes);
            Assert.Equal(2, routes.Count(route => route.Pattern.StartsWith("/health/", StringComparison.Ordinal)));
            Assert.DoesNotContain(routes, route => route.Pattern.StartsWith("/api/diagnostics/", StringComparison.Ordinal));

            foreach (var route in routes)
            {
                var mutation = route.Methods.Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE");
                var anonymousAuthEstablishment = route.Pattern is "/api/auth/bootstrap" or "/api/auth/login";
                var authenticatedSelfService = route.Pattern is "/api/auth/logout" or "/api/auth/password";
                var anonymousRead = route.Pattern is "/api/auth/csrf" or "/api/auth/bootstrap/status" ||
                                    route.Pattern.StartsWith("/health/", StringComparison.Ordinal);

                if (mutation)
                {
                    Assert.True(route.RequiresAntiforgery, $"{route.Pattern} is a browser mutation without antiforgery metadata.");
                    if (anonymousAuthEstablishment)
                    {
                        Assert.True(route.AllowsAnonymous);
                    }
                    else if (authenticatedSelfService)
                    {
                        AssertPolicy(route, LocalAuthPolicies.Authenticated);
                    }
                    else
                    {
                        AssertPolicy(route, LocalAuthPolicies.Admin);
                    }
                }
                else if (anonymousRead)
                {
                    Assert.Empty(route.Authorization);
                }
                else if (route.Pattern == "/api/users" || route.Pattern.StartsWith("/openapi/", StringComparison.Ordinal))
                {
                    AssertPolicy(route, LocalAuthPolicies.Admin);
                }
                else
                {
                    AssertPolicy(route, LocalAuthPolicies.Authenticated);
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertPolicy(ProductRoute route, string policy) =>
        Assert.Contains(route.Authorization, authorization =>
            string.Equals(authorization.Policy, policy, StringComparison.Ordinal));

    private sealed record ProductRoute(
        string Pattern,
        IReadOnlyList<string> Methods,
        IReadOnlyList<IAuthorizeData> Authorization,
        bool AllowsAnonymous,
        bool RequiresAntiforgery);
}

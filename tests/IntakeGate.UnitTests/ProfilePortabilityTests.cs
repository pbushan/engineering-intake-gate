using System.Text.Json;
using IntakeGate.Application.Profiles;
using IntakeGate.Application.Setup;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class ProfilePortabilityTests
{
    [Fact]
    public void PortableV1ExportsIncompleteConfigurationAndRoundTripsOnlyAllowListedFields()
    {
        var values = AuthoritativeOnboardingDefaults.Create().Values with
        {
            ProfileVersion = "portable-v1",
            PolicyUrl = "https://example.invalid/policy",
            Policy = new OnboardingPolicy("policy", "1", [])
        };
        var exportedAt = new DateTimeOffset(2026, 9, 18, 12, 30, 0, TimeSpan.Zero);

        var document = ProfilePortability.Export(values, exportedAt);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(ProfilePortability.Format, parsed.RootElement.GetProperty("format").GetString());
        Assert.Equal(ProfilePortability.Version, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(exportedAt, parsed.RootElement.GetProperty("exportedAt").GetDateTimeOffset());
        Assert.Equal("portable-v1", parsed.RootElement.GetProperty("profile").GetProperty("profileVersion").GetString());
        Assert.Equal("policy", parsed.RootElement.GetProperty("policy").GetProperty("id").GetString());
        Assert.False(parsed.RootElement.GetProperty("profile").TryGetProperty("policy", out _));
        foreach (var excluded in new[] { "secret", "password", "token", "credential", "profileId", "history", "cache", "path" })
            Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);

        var roundTripped = ProfilePortability.ToDraft(document);
        Assert.Equal(JsonSerializer.Serialize(values), JsonSerializer.Serialize(roundTripped));
    }
}

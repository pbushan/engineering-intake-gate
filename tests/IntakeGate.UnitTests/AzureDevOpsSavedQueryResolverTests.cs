using IntakeGate.Application.AzureDevOps;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class AzureDevOpsSavedQueryResolverTests
{
    private static readonly Guid QueryId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void DISC_009_GuidAndConfiguredProjectUrlResolveToCanonicalIdentity()
    {
        Assert.True(AzureDevOpsSavedQueryResolver.TryResolve(QueryId.ToString("B"),
            new Uri("https://dev.azure.com/example"), "Engineering", out var fromGuid));
        Assert.True(AzureDevOpsSavedQueryResolver.TryResolve(
            $"https://dev.azure.com/example/Engineering/_queries/query/{QueryId:D}/",
            new Uri("https://dev.azure.com/example/"), "Engineering", out var fromUrl));
        Assert.Equal(QueryId, fromGuid);
        Assert.Equal(QueryId, fromUrl);
    }

    [Theory]
    [InlineData("https://other.example/example/Engineering/_queries/query/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("https://dev.azure.com/other/Engineering/_queries/query/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("https://dev.azure.com/example/Other/_queries/query/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("Select * From WorkItems")]
    [InlineData("not-a-query")]
    public void DISC_009_UnrelatedMalformedAndWiqlInputsAreRejected(string input)
    {
        Assert.False(AzureDevOpsSavedQueryResolver.TryResolve(input,
            new Uri("https://dev.azure.com/example"), "Engineering", out _));
    }
}

using IntakeGate.Application.AzureDevOps;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class AzureDevOpsWorkItemResolverTests
{
    [Theory]
    [InlineData("42", 42)]
    [InlineData(" https://dev.azure.com/example/Project%20One/_workitems/edit/42 ", 42)]
    public void OPS_003_SupportedIdentityResolvesInsideConfiguredBoundary(string input, int expected)
    {
        Assert.True(AzureDevOpsWorkItemResolver.TryResolve(
            input, new Uri("https://dev.azure.com/example"), "Project One", out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("https://example.invalid/example/Project%20One/_workitems/edit/42")]
    [InlineData("https://dev.azure.com/another/Project%20One/_workitems/edit/42")]
    [InlineData("https://dev.azure.com/example/Other/_workitems/edit/42")]
    [InlineData("https://dev.azure.com/example/Project%20One/_workitems/edit/42?redirect=x")]
    [InlineData("https://user@dev.azure.com/example/Project%20One/_workitems/edit/42")]
    [InlineData("https://dev.azure.com/example/Project%20One/_queries/query/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public void OPS_003_UnrelatedOrMalformedIdentityIsRejected(string input)
    {
        Assert.False(AzureDevOpsWorkItemResolver.TryResolve(
            input, new Uri("https://dev.azure.com/example"), "Project One", out _));
    }

    [Fact]
    public void OPS_008_CanonicalLinkUsesOnlyConfiguredAuthorityAndIdentity()
    {
        var link = AzureDevOpsWorkItemResolver.CreateCanonicalUrl(
            new Uri("https://dev.azure.com/example/"), "Project One", 42);
        Assert.Equal("https://dev.azure.com/example/Project%20One/_workitems/edit/42", link.AbsoluteUri);
    }
}

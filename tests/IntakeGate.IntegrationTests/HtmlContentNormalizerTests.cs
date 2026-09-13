using IntakeGate.Application.Evidence;
using IntakeGate.Infrastructure.Evidence;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class HtmlContentNormalizerTests
{
    [Fact]
    public void E19_HtmlPreservesUsefulStructureAndDropsExecutablePresentationContent()
    {
        var html = """
            <style>.danger { color:red }</style>
            <script>throw new Error('must not survive')</script>
            <h2>Failure details</h2>
            <p>Request failed<br>ERROR 417: synthetic failure</p>
            <ol><li>Open page</li><li>Submit form</li></ol>
            <pre>line 1
              at Synthetic.Stack()</pre>
            <table><tr><th>Code</th><th>Meaning</th></tr><tr><td>417</td><td>Failed</td></tr></table>
            <a href="https://example.invalid/details">Details</a>
            <a href="javascript:alert('not executable')">Unsafe link text remains</a>
            <img src="http://127.0.0.1:1/must-not-be-loaded" alt="screenshot metadata">
            """;

        var normalized = new HtmlContentNormalizer().Normalize(html, WorkItemContentFormat.Html);

        Assert.Contains("Failure details", normalized, StringComparison.Ordinal);
        Assert.Contains("ERROR 417: synthetic failure", normalized, StringComparison.Ordinal);
        Assert.Contains("1. Open page", normalized, StringComparison.Ordinal);
        Assert.Contains("2. Submit form", normalized, StringComparison.Ordinal);
        Assert.Contains("at Synthetic.Stack()", normalized, StringComparison.Ordinal);
        Assert.Contains("Code\tMeaning", normalized, StringComparison.Ordinal);
        Assert.Contains("Details (https://example.invalid/details)", normalized, StringComparison.Ordinal);
        Assert.Contains("Unsafe link text remains", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshot metadata", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("must not survive", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-loaded", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void E19_NormalizationIsStableAcrossLineEndings()
    {
        var normalizer = new HtmlContentNormalizer();

        Assert.Equal(
            normalizer.Normalize("<p>one</p>\r\n<p>two</p>", WorkItemContentFormat.Html),
            normalizer.Normalize("<p>one</p>\n<p>two</p>", WorkItemContentFormat.Html));
    }
}

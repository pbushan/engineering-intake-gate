using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class HtmlContentNormalizer : IContentNormalizer
{
    public string Normalize(string? content, WorkItemContentFormat format)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (format != WorkItemContentFormat.Html)
        {
            return NormalizeLines(content);
        }

        // HtmlParser parses the supplied string directly. No browsing context, loader, scripting,
        // or network-capable service is configured.
        var document = new HtmlParser().ParseDocument(content);
        var builder = new StringBuilder(content.Length);
        foreach (var child in (document.Body ?? document.DocumentElement).ChildNodes)
        {
            AppendNode(child, builder);
        }

        return NormalizeLines(builder.ToString());
    }

    private static void AppendNode(INode node, StringBuilder builder)
    {
        if (node is IText text)
        {
            AppendCollapsedText(text.Data, builder);
            return;
        }

        if (node is not IElement element)
        {
            return;
        }

        var name = element.LocalName;
        if (name is "script" or "style" or "template" or "noscript" or "svg" or "canvas")
        {
            return;
        }

        if (name is "pre")
        {
            EnsureLineBreak(builder);
            builder.Append(element.TextContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            EnsureLineBreak(builder);
            return;
        }

        if (name is "br")
        {
            EnsureLineBreak(builder);
            return;
        }

        var block = IsBlock(name);
        if (block)
        {
            EnsureLineBreak(builder);
        }

        if (name == "li")
        {
            builder.Append(GetListPrefix(element));
        }

        if (name == "img")
        {
            var alternative = element.GetAttribute("alt");
            if (!string.IsNullOrWhiteSpace(alternative))
            {
                AppendCollapsedText(alternative, builder);
            }
        }
        else
        {
            foreach (var child in element.ChildNodes)
            {
                AppendNode(child, builder);
            }
        }

        if (name == "a")
        {
            var href = element.GetAttribute("href");
            if (IsSafeLink(href) && !string.Equals(href!.Trim(), element.TextContent.Trim(), StringComparison.Ordinal))
            {
                builder.Append(" (");
                builder.Append(href.Trim());
                builder.Append(')');
            }
        }

        if (name is "td" or "th")
        {
            builder.Append('\t');
        }

        if (block)
        {
            EnsureLineBreak(builder);
        }
    }

    private static string GetListPrefix(IElement item)
    {
        if (item.ParentElement?.LocalName != "ol")
        {
            return "- ";
        }

        var index = 1;
        foreach (var sibling in item.ParentElement.Children)
        {
            if (ReferenceEquals(sibling, item)) break;
            if (sibling.LocalName == "li") index++;
        }

        return $"{index}. ";
    }

    private static bool IsBlock(string name) => name is
        "address" or "article" or "aside" or "blockquote" or "div" or "dl" or "dt" or "dd" or
        "fieldset" or "figcaption" or "figure" or "footer" or "form" or "h1" or "h2" or "h3" or
        "h4" or "h5" or "h6" or "header" or "hr" or "li" or "main" or "nav" or "ol" or "p" or
        "section" or "table" or "tbody" or "thead" or "tfoot" or "tr" or "ul";

    private static bool IsSafeLink(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;
        var trimmed = href.TrimStart();
        return !trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCollapsedText(string value, StringBuilder builder)
    {
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character == '\u00a0')
            {
                pendingSpace = builder.Length > 0 && builder[^1] != '\n' && builder[^1] != '\t';
                continue;
            }

            if (pendingSpace && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingSpace = false;
        }
    }

    private static void EnsureLineBreak(StringBuilder builder)
    {
        while (builder.Length > 0 && (builder[^1] == ' ' || builder[^1] == '\t'))
        {
            builder.Length--;
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private static string NormalizeLines(string content)
    {
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ');
        var result = new StringBuilder(normalized.Length);
        var blankLines = 0;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = RemoveUnsafeControls(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                blankLines++;
                if (blankLines > 1) continue;
            }
            else
            {
                blankLines = 0;
            }

            result.AppendLine(line);
        }

        return result.ToString().Trim();
    }

    private static string RemoveUnsafeControls(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            if (!char.IsControl(character) || character == '\t')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

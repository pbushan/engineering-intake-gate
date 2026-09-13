using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class TextAttachmentProcessor : IAttachmentProcessor
{
    public bool CanProcess(DetectedAttachment attachment) =>
        attachment.MediaType is "text/plain" or "text/x-log" or "application/json" or "application/xml" or "text/csv";

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment,
        AttachmentLimits limits,
        int maximumExtractedCharacters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDecode(attachment.Content, out var text))
            return ValueTask.FromResult(Error("TextEncodingInvalid"));

        return ValueTask.FromResult(attachment.MediaType switch
        {
            "application/json" => Json(text, limits.MaximumStructuredTextDepth),
            "application/xml" => Xml(text, attachment.Content.LongLength),
            "text/csv" => Csv(text, limits.MaximumCsvRows),
            _ => new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.Text,
                NormalizeLines(text), false, false, null, null, null)
        });
    }

    private static AttachmentProcessorOutput Json(string text, int maximumDepth)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                WriteCanonical(document.RootElement, writer);
            return new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Processed, AttachmentInspectionMode.StructuredText,
                Encoding.UTF8.GetString(stream.ToArray()), false, false, null, null, null);
        }
        catch (JsonException)
        {
            return new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Partial, AttachmentInspectionMode.Text,
                NormalizeLines(text), false, false, null, null, "MalformedJsonTextFallback");
        }
    }

    private static AttachmentProcessorOutput Xml(string text, long byteLength)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = Math.Max(1, byteLength * 2),
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            return new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Processed, AttachmentInspectionMode.StructuredText,
                document.ToString(SaveOptions.None), false, false, null, null, null);
        }
        catch (XmlException)
        {
            return Error("MalformedOrUnsafeXml");
        }
    }

    private static AttachmentProcessorOutput Csv(string text, int maximumRows)
    {
        var delimiter = DetectDelimiter(text);
        try
        {
            var rows = ParseCsv(text, delimiter).ToArray();
            var inspected = rows.Take(maximumRows).ToArray();
            var output = string.Join('\n', inspected.Select(row => string.Join(" | ", row)));
            var partial = inspected.Length < rows.Length;
            return new AttachmentProcessorOutput(
                partial ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.StructuredText,
                output,
                partial,
                partial,
                null,
                null,
                partial ? "CsvRowLimitExceeded" : null);
        }
        catch (FormatException)
        {
            return new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Partial, AttachmentInspectionMode.Text,
                NormalizeLines(text), false, false, null, null, "MalformedCsvTextFallback");
        }
    }

    private static IEnumerable<string[]> ParseCsv(string value, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < value.Length && value[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"') quoted = false;
                else field.Append(character);
                continue;
            }

            if (character == '"' && field.Length == 0) quoted = true;
            else if (character == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                yield return row.ToArray();
                row.Clear();
            }
            else field.Append(character);
        }
        if (quoted) throw new FormatException("Unterminated CSV quote.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    private static char DetectDelimiter(string text)
    {
        var line = text.Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var candidates = new[] { ',', '\t', ';' };
        return candidates.OrderByDescending(candidate => line.Count(character => character == candidate)).ThenBy(candidate => Array.IndexOf(candidates, candidate)).First();
    }

    private static bool TryDecode(byte[] content, out string text)
    {
        try
        {
            if (content.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
                text = new UnicodeEncoding(false, true, true).GetString(content, 2, content.Length - 2);
            else if (content.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
                text = new UnicodeEncoding(true, true, true).GetString(content, 2, content.Length - 2);
            else
            {
                var offset = content.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(content, offset, content.Length - offset);
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static AttachmentProcessorOutput Error(string category) => new(
        AttachmentProcessingStatus.Error, AttachmentInspectionMode.None, string.Empty,
        false, false, null, null, category);

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

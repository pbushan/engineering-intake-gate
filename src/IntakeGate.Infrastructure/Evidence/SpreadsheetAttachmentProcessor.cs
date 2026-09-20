using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

/// <summary>Reads cached/literal Open XML workbook values only; calculation and external links are never invoked.</summary>
public sealed class SpreadsheetAttachmentProcessor : IAttachmentProcessor, IAttachmentLimitsFingerprintProvider
{
    public string ProcessorVersion => "xlsx-v1";
    public bool CanProcess(DetectedAttachment attachment) =>
        attachment.MediaType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string CreateLimitsFingerprint(AttachmentLimits limits, int maximumExtractedCharacters) =>
        AnalysisFingerprint.Sha256($"{limits.MaximumSpreadsheetSheets}|{limits.MaximumSpreadsheetRowsPerSheet}|" +
            $"{limits.MaximumSpreadsheetColumns}|{limits.MaximumSpreadsheetCells}|{maximumExtractedCharacters}");

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(
        DetectedAttachment attachment, AttachmentLimits limits, int maximumExtractedCharacters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            OpenXmlPackageSafety.Validate(attachment.Content);
            using var stream = new MemoryStream(attachment.Content, writable: false);
            using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings
            {
                AutoSave = false,
                MaxCharactersInPart = Math.Max(1, Math.Min(maximumExtractedCharacters * 20L, 20_000_000L))
            });
            var workbookPart = document.WorkbookPart ?? throw new InvalidDataException();
            var workbook = workbookPart.Workbook ?? throw new InvalidDataException();
            var shared = workbookPart.SharedStringTablePart?.SharedStringTable;
            var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
            var builder = new StringBuilder(Math.Min(maximumExtractedCharacters, 32_768));
            builder.Append("Workbook: ").Append(attachment.Name).Append('\n')
                .Append("Sheets available: ").Append(sheets.Length).Append('\n');
            var truncated = sheets.Length > limits.MaximumSpreadsheetSheets;
            var cellCount = 0;
            var sheetsInspected = 0;

            foreach (var sheet in sheets.Take(limits.MaximumSpreadsheetSheets))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheet.Id?.Value is not { Length: > 0 } relationshipId) continue;
                if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart) continue;
                var worksheet = worksheetPart.Worksheet ?? throw new InvalidDataException();
                sheetsInspected++;
                builder.Append("\n[Sheet: ").Append(Safe(sheet.Name?.Value ?? $"Sheet {sheetsInspected}"))
                    .Append("]\n");
                var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToArray() ?? [];
                var inspectedRows = 0;
                var maximumColumnSeen = 0;
                foreach (var row in rows)
                {
                    if (inspectedRows >= limits.MaximumSpreadsheetRowsPerSheet ||
                        cellCount >= limits.MaximumSpreadsheetCells || builder.Length >= maximumExtractedCharacters)
                    {
                        truncated = true;
                        break;
                    }
                    var values = new List<string>();
                    foreach (var cell in row.Elements<Cell>())
                    {
                        var column = ColumnIndex(cell.CellReference?.Value);
                        if (column <= 0 || column > limits.MaximumSpreadsheetColumns)
                        {
                            truncated = true;
                            continue;
                        }
                        while (values.Count < column - 1) values.Add(string.Empty);
                        if (cellCount++ >= limits.MaximumSpreadsheetCells)
                        {
                            truncated = true;
                            break;
                        }
                        maximumColumnSeen = Math.Max(maximumColumnSeen, column);
                        values.Add(ReadCell(cell, shared));
                    }
                    if (values.Count > 0)
                    {
                        var line = $"R{row.RowIndex?.Value ?? (uint)(inspectedRows + 1)}: {string.Join(" | ", values.Select(Safe))}\n";
                        AppendBounded(builder, line, maximumExtractedCharacters, ref truncated);
                    }
                    inspectedRows++;
                }
                builder.Append("Inspected range: rows=").Append(inspectedRows.ToString(CultureInfo.InvariantCulture))
                    .Append(", columns=").Append(maximumColumnSeen.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (rows.Length > inspectedRows) truncated = true;
            }

            builder.Append("\nSampling: sheets=").Append(sheetsInspected).Append('/').Append(sheets.Length)
                .Append(", cells=").Append(cellCount)
                .Append(", formulas=literal with cached value; formulas were not executed.");
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                truncated ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Processed,
                AttachmentInspectionMode.Spreadsheet, builder.ToString(), truncated, truncated,
                null, null, truncated ? "SpreadsheetSamplingLimitReached" : null)
            {
                Warnings = truncated ? ["SpreadsheetSamplingLimitReached"] : []
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Error, AttachmentInspectionMode.None, string.Empty,
                false, false, null, null, "MalformedOrUnsafeSpreadsheet"));
        }
    }

    private static string ReadCell(Cell cell, SharedStringTable? shared)
    {
        var value = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index) &&
            shared is not null && index >= 0 && index < shared.ChildElements.Count)
            value = shared.ChildElements[index].InnerText;
        else if (cell.DataType?.Value == CellValues.InlineString)
            value = cell.InlineString?.InnerText ?? string.Empty;
        else if (cell.DataType?.Value == CellValues.Boolean)
            value = value == "1" ? "TRUE" : "FALSE";
        var formula = cell.CellFormula?.Text;
        return string.IsNullOrWhiteSpace(formula) ? value : $"={formula} [cached: {value}]";
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return 0;
        var result = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character)) break;
            result = checked(result * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return result;
    }

    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void AppendBounded(StringBuilder builder, string value, int maximum, ref bool truncated)
    {
        var remaining = maximum - builder.Length;
        if (remaining <= 0) { truncated = true; return; }
        if (value.Length > remaining) truncated = true;
        builder.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
    }
}

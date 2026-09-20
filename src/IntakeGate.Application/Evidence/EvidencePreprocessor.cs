using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IntakeGate.Application.Configuration;

namespace IntakeGate.Application.Evidence;

public sealed class EvidencePreprocessor : IEvidencePreprocessor
{
    private readonly IContentNormalizer contentNormalizer;
    private readonly ISecretRedactor secretRedactor;
    private readonly IEvidenceProcessingLog processingLog;
    private readonly IAttachmentProcessingService attachmentProcessingService;

    public EvidencePreprocessor(
        IContentNormalizer contentNormalizer,
        ISecretRedactor secretRedactor,
        IEvidenceProcessingLog processingLog)
        : this(contentNormalizer, secretRedactor, processingLog, new MetadataOnlyAttachmentProcessingService()) { }

    public EvidencePreprocessor(
        IContentNormalizer contentNormalizer,
        ISecretRedactor secretRedactor,
        IEvidenceProcessingLog processingLog,
        IAttachmentProcessingService attachmentProcessingService)
    {
        this.contentNormalizer = contentNormalizer;
        this.secretRedactor = secretRedactor;
        this.processingLog = processingLog;
        this.attachmentProcessingService = attachmentProcessingService;
    }

    public EvaluationEvidence Prepare(RawWorkItem workItem, ProcessingConfiguration processing) =>
        PrepareAsync(workItem, processing).AsTask().GetAwaiter().GetResult();

    public async ValueTask<EvaluationEvidence> PrepareAsync(
        RawWorkItem workItem,
        ProcessingConfiguration processing,
        CancellationToken cancellationToken = default)
        => await PrepareCoreAsync(workItem, processing, null, cancellationToken);

    public async ValueTask<EvaluationEvidence> PrepareAsync(
        RawWorkItem workItem,
        ProcessingConfiguration processing,
        EvidencePreparationOptions options,
        CancellationToken cancellationToken = default)
        => await PrepareCoreAsync(workItem, processing, options, cancellationToken);

    private async ValueTask<EvaluationEvidence> PrepareCoreAsync(
        RawWorkItem workItem,
        ProcessingConfiguration processing,
        EvidencePreparationOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(processing);
        ValidateLimits(processing);

        var humanComments = workItem.Comments
            .Where(comment => !ValidatorCommentMarker.IsPresent(comment.Content))
            .OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(comment => comment.Id, StringComparer.Ordinal)
            .ThenBy(comment => comment.Content, StringComparer.Ordinal)
            .ToArray();
        var excludedValidatorCommentCount = workItem.Comments.Count - humanComments.Length;
        var selectedComments = humanComments.Take(processing.ContentLimits.MaximumComments).ToArray();

        var selectedAttachments = workItem.Attachments
            .OrderBy(attachment => attachment.Reference, StringComparer.Ordinal)
            .ThenBy(attachment => attachment.FileName, StringComparer.Ordinal)
            .Take(processing.AttachmentLimits.MaximumCount)
            .ToArray();
        var processedAttachments = options is not null && attachmentProcessingService is ICacheAwareAttachmentProcessingService cacheAware
            ? await cacheAware.ProcessAsync(selectedAttachments, processing.AttachmentLimits,
                processing.ContentLimits.MaximumExtractedTextCharacters, options, cancellationToken)
            : await attachmentProcessingService.ProcessAsync(selectedAttachments, processing.AttachmentLimits,
                processing.ContentLimits.MaximumExtractedTextCharacters, cancellationToken);

        var redactions = new RedactionAccumulator();
        foreach (var processedAttachment in processedAttachments)
            redactions.Add(processedAttachment.RedactionCategoryCounts);
        var budget = new TextBudget(
            processing.ContentLimits.MaximumTotalCharacters,
            processing.ContentLimits.MaximumExtractedTextCharacters);

        var workItemType = AddText(workItem.WorkItemType, WorkItemContentFormat.PlainText, budget, redactions, out var workItemTypeTruncated);
        var title = AddText(workItem.Title, WorkItemContentFormat.PlainText, budget, redactions, out var titleTruncated);
        var description = AddText(workItem.Description, workItem.DescriptionFormat, budget, redactions, out var descriptionTruncated);

        var fields = new List<EvaluationEvidenceField>();
        var truncatedFieldCount = 0;
        var orderedFields = workItem.Fields
            .OrderBy(field => field.ReferenceName, StringComparer.Ordinal)
            .ThenBy(field => field.DisplayName, StringComparer.Ordinal)
            .ThenBy(field => Canonicalize(field.Value), StringComparer.Ordinal)
            .ToArray();
        foreach (var field in orderedFields)
        {
            if (!budget.TryBeginEntry())
            {
                break;
            }

            var referenceName = AddText(field.ReferenceName, WorkItemContentFormat.PlainText, budget, redactions, out var referenceTruncated);
            var displayName = AddOptionalText(field.DisplayName, budget, redactions, out var displayTruncated);
            var rawValue = field.Value.ValueKind == JsonValueKind.String
                ? field.Value.GetString() ?? string.Empty
                : Canonicalize(field.Value);
            var valueFormat = field.Value.ValueKind == JsonValueKind.String ? field.Format : WorkItemContentFormat.Json;
            var value = AddText(rawValue, valueFormat, budget, redactions, out var valueTruncated);
            var truncated = referenceTruncated || displayTruncated || valueTruncated;
            if (truncated)
            {
                truncatedFieldCount++;
            }

            fields.Add(new EvaluationEvidenceField(
                referenceName,
                displayName,
                value,
                field.Value.ValueKind.ToString(),
                field.Format,
                truncated));
        }

        var tags = new List<string>();
        var truncatedTagCount = 0;
        foreach (var rawTag in workItem.Tags.OrderBy(tag => tag, StringComparer.Ordinal))
        {
            if (!budget.TryBeginEntry())
            {
                break;
            }

            tags.Add(AddText(rawTag, WorkItemContentFormat.PlainText, budget, redactions, out var truncated));
            if (truncated)
            {
                truncatedTagCount++;
            }
        }

        var relations = new List<EvaluationEvidenceRelation>();
        var truncatedRelationCount = 0;
        foreach (var relation in workItem.Relations
                     .OrderBy(relation => relation.Reference, StringComparer.Ordinal)
                     .ThenBy(relation => relation.RelationType, StringComparer.Ordinal)
                     .ThenBy(relation => relation.DisplayName, StringComparer.Ordinal))
        {
            if (!budget.TryBeginEntry())
            {
                break;
            }

            var reference = AddText(relation.Reference, WorkItemContentFormat.PlainText, budget, redactions, out var referenceTruncated);
            var relationType = AddText(relation.RelationType, WorkItemContentFormat.PlainText, budget, redactions, out var typeTruncated);
            var displayName = AddOptionalText(relation.DisplayName, budget, redactions, out var displayTruncated);
            var truncated = referenceTruncated || typeTruncated || displayTruncated;
            if (truncated)
            {
                truncatedRelationCount++;
            }

            relations.Add(new EvaluationEvidenceRelation(reference, relationType, displayName, truncated));
        }

        var comments = new List<EvaluationEvidenceComment>();
        var truncatedCommentCount = 0;
        foreach (var comment in selectedComments)
        {
            if (!budget.TryBeginEntry())
            {
                break;
            }

            var author = AddOptionalText(comment.AuthorDisplayName, budget, redactions, out var authorTruncated);
            var content = AddText(comment.Content, comment.Format, budget, redactions, out var contentTruncated);
            var truncated = authorTruncated || contentTruncated;
            if (truncated)
            {
                truncatedCommentCount++;
            }

            comments.Add(new EvaluationEvidenceComment(comment.Id, author, comment.CreatedAt, content, truncated));
        }

        var attachments = new List<EvaluationAttachmentMetadata>();
        var visualEvidence = new List<VisualEvidence>();
        var truncatedAttachmentCount = 0;
        foreach (var result in processedAttachments)
        {
            if (!budget.TryBeginEntry())
            {
                break;
            }

            var reference = AddText(result.AttachmentId, WorkItemContentFormat.PlainText, budget, redactions, out var referenceTruncated);
            var fileName = AddText(result.Name, WorkItemContentFormat.PlainText, budget, redactions, out var fileNameTruncated);
            var contentType = AddOptionalText(result.MediaType, budget, redactions, out var contentTypeTruncated);
            var extractedEvidence = AddText(result.ExtractedEvidence, WorkItemContentFormat.PlainText, budget, redactions, out var evidenceTruncated);
            var truncated = result.Truncated || referenceTruncated || fileNameTruncated || contentTypeTruncated || evidenceTruncated;
            if (truncated)
            {
                truncatedAttachmentCount++;
            }

            var status = evidenceTruncated && result.ProcessingStatus == AttachmentProcessingStatus.Processed
                ? AttachmentProcessingStatus.Partial
                : result.ProcessingStatus;
            var failureCategory = evidenceTruncated
                ? "EvidenceCharacterLimitExceeded"
                : result.FailureCategory;
            attachments.Add(new EvaluationAttachmentMetadata(
                reference,
                fileName,
                contentType,
                result.OriginalSize,
                truncated,
                status,
                result.InspectionMode,
                extractedEvidence,
                result.Sampled,
                result.PagesAvailable,
                result.PagesInspected,
                failureCategory,
                result.VisualContent is not null)
            {
                ContentSha256 = result.ContentSha256,
                ArtifactId = result.ArtifactId,
                ProcessorIdentity = result.ProcessorIdentity,
                ProcessorVersion = result.ProcessorVersion,
                CacheReused = result.CacheReused,
                Warnings = result.Warnings,
                Pdf = result.Pdf,
                Audio = result.Audio,
                Video = result.Video,
                SelectedKeyScreenshots = result.SelectedKeyScreenshots,
                AiInteractions = result.AiInteractions
            });

            if (result.VisualContent is not null)
            {
                visualEvidence.Add(new VisualEvidence(reference, fileName, result.VisualContent.MediaType, result.VisualContent.Content.Span));
            }

            processingLog.AttachmentProcessingCompleted(new AttachmentProcessingLogEntry(
                workItem.WorkItemId,
                workItem.Revision,
                reference,
                fileName,
                contentType,
                result.OriginalSize,
                status,
                result.InspectionMode,
                truncated,
                result.Sampled,
                result.PagesAvailable,
                result.PagesInspected,
                failureCategory,
                result.ProcessingDurationMilliseconds));
        }

        var omittedFieldCount = orderedFields.Length - fields.Count;
        var omittedTagCount = workItem.Tags.Count - tags.Count;
        var omittedRelationCount = workItem.Relations.Count - relations.Count;
        var omittedCommentCount = humanComments.Length - comments.Count;
        var omittedAttachmentCount = workItem.Attachments.Count - attachments.Count;
        var anyTruncation = budget.AggregateLimitReached || workItemTypeTruncated || titleTruncated || descriptionTruncated ||
                            truncatedFieldCount > 0 || omittedFieldCount > 0 || truncatedTagCount > 0 || omittedTagCount > 0 ||
                            truncatedRelationCount > 0 || omittedRelationCount > 0 || truncatedCommentCount > 0 ||
                            omittedCommentCount > 0 || truncatedAttachmentCount > 0 || omittedAttachmentCount > 0;

        var disclosure = new EvidenceProcessingDisclosure(
            anyTruncation,
            budget.AggregateLimitReached,
            workItemTypeTruncated,
            titleTruncated,
            descriptionTruncated,
            truncatedFieldCount,
            omittedFieldCount,
            tags.Count,
            workItem.Tags.Count,
            truncatedTagCount,
            omittedTagCount,
            relations.Count,
            workItem.Relations.Count,
            truncatedRelationCount,
            omittedRelationCount,
            comments.Count,
            humanComments.Length,
            excludedValidatorCommentCount,
            truncatedCommentCount,
            omittedCommentCount,
            attachments.Count,
            workItem.Attachments.Count,
            truncatedAttachmentCount,
            omittedAttachmentCount,
            workItem.Attachments.Count > 0,
            AttachmentContentInspected: attachments.Any(attachment => attachment.InspectionMode != AttachmentInspectionMode.None),
            budget.IncludedCharacters);
        var redactionMetadata = redactions.ToMetadata();

        processingLog.ContentCollectionCompleted(new ContentCollectionLogEntry(
            workItem.WorkItemId,
            workItem.Revision,
            fields.Count,
            comments.Count,
            excludedValidatorCommentCount,
            attachments.Count,
            anyTruncation));
        processingLog.SecretRedactionCompleted(new SecretRedactionLogEntry(
            workItem.WorkItemId,
            workItem.Revision,
            redactionMetadata.RedactionOccurred,
            redactionMetadata.RedactionCount));

        return new EvaluationEvidence(
            workItem.WorkItemId,
            workItem.Revision,
            workItemType,
            title,
            Array.AsReadOnly(fields.ToArray()),
            description,
            Array.AsReadOnly(tags.ToArray()),
            Array.AsReadOnly(relations.ToArray()),
            Array.AsReadOnly(comments.ToArray()),
            Array.AsReadOnly(attachments.ToArray()),
            disclosure,
            redactionMetadata)
        {
            VisualEvidence = Array.AsReadOnly(visualEvidence.ToArray())
        };
    }

    private string AddText(
        string content,
        WorkItemContentFormat format,
        TextBudget budget,
        RedactionAccumulator redactions,
        out bool truncated)
    {
        var normalized = contentNormalizer.Normalize(content, format);
        var redacted = secretRedactor.Redact(normalized);
        redactions.Add(redacted);
        return budget.Add(redacted.Content, out truncated);
    }

    private string? AddOptionalText(
        string? content,
        TextBudget budget,
        RedactionAccumulator redactions,
        out bool truncated)
    {
        if (content is null)
        {
            truncated = false;
            return null;
        }

        return AddText(content, WorkItemContentFormat.PlainText, budget, redactions, out truncated);
    }

    private static string Canonicalize(JsonElement value)
    {
        var builder = new StringBuilder();
        WriteCanonical(value, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    WriteCanonical(property.Value, builder);
                    firstProperty = false;
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    WriteCanonical(item, builder);
                    firstItem = false;
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                else if (value.TryGetDecimal(out var decimalValue)) builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                else builder.Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static void ValidateLimits(ProcessingConfiguration processing)
    {
        if (processing.ContentLimits.MaximumTotalCharacters <= 0 ||
            processing.ContentLimits.MaximumComments <= 0 ||
            processing.ContentLimits.MaximumExtractedTextCharacters <= 0 ||
            processing.AttachmentLimits.MaximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processing), "Evidence processing limits must be positive.");
        }
    }

    private sealed class MetadataOnlyAttachmentProcessingService : IAttachmentProcessingService
    {
        public ValueTask<IReadOnlyList<AttachmentProcessingResult>> ProcessAsync(
            IReadOnlyList<RawAttachmentMetadata> attachments,
            AttachmentLimits limits,
            int maximumExtractedCharacters,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AttachmentProcessingResult>>(attachments.Select(attachment =>
                new AttachmentProcessingResult(
                    attachment.Reference,
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.SizeBytes,
                    AttachmentProcessingStatus.Unavailable,
                    AttachmentInspectionMode.None,
                    string.Empty,
                    false,
                    false,
                    null,
                    null,
                    "AttachmentContentNotProvided")).ToArray());
    }

    private sealed class TextBudget(int maximumTotalCharacters, int maximumContentCharacters)
    {
        public int IncludedCharacters { get; private set; }
        public bool AggregateLimitReached { get; private set; }
        public bool Exhausted => IncludedCharacters >= maximumTotalCharacters;

        public bool TryBeginEntry()
        {
            if (Exhausted)
            {
                AggregateLimitReached = true;
                return false;
            }

            // Count one deterministic structural separator so empty metadata entries cannot bypass
            // the aggregate bound and create an unbounded evidence package.
            IncludedCharacters++;
            return true;
        }

        public string Add(string content, out bool truncated)
        {
            var perContentLength = Math.Min(content.Length, maximumContentCharacters);
            var allowed = Math.Min(perContentLength, maximumTotalCharacters - IncludedCharacters);
            var safeLength = SafeUtf16Length(content, allowed);
            truncated = safeLength < content.Length;
            if (perContentLength > maximumTotalCharacters - IncludedCharacters)
            {
                AggregateLimitReached = true;
            }

            IncludedCharacters += safeLength;
            return safeLength == content.Length ? content : content[..safeLength];
        }

        private static int SafeUtf16Length(string value, int length) =>
            length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
    }

    private sealed class RedactionAccumulator
    {
        private readonly SortedDictionary<string, int> counts = new(StringComparer.Ordinal);

        public void Add(SecretRedactionResult result)
        {
            foreach (var category in result.CategoryCounts)
            {
                counts[category.Key] = counts.GetValueOrDefault(category.Key) + category.Value;
            }
        }

        public void Add(IReadOnlyDictionary<string, int> categoryCounts)
        {
            foreach (var category in categoryCounts)
                counts[category.Key] = counts.GetValueOrDefault(category.Key) + category.Value;
        }

        public RedactionMetadata ToMetadata() => new(
            counts.Count > 0,
            counts.Values.Sum(),
            new ReadOnlyDictionary<string, int>(counts));
    }
}

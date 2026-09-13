using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntakeGate.Application.Evidence;

/// <summary>
/// Provider-neutral, untrusted work-item input. A provider adapter may populate this type, but it
/// must not be persisted, logged, or supplied to an AI boundary.
/// </summary>
public sealed record RawWorkItem
{
    public RawWorkItem(
        string workItemId,
        string revision,
        string workItemType,
        string title,
        IReadOnlyList<RawWorkItemField>? fields = null,
        string? description = null,
        WorkItemContentFormat descriptionFormat = WorkItemContentFormat.PlainText,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<RawWorkItemRelation>? relations = null,
        IReadOnlyList<RawWorkItemComment>? comments = null,
        IReadOnlyList<RawAttachmentMetadata>? attachments = null,
        DateTimeOffset? changedAtUtc = null)
    {
        WorkItemId = workItemId ?? throw new ArgumentNullException(nameof(workItemId));
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        WorkItemType = workItemType ?? string.Empty;
        Title = title ?? string.Empty;
        Fields = Array.AsReadOnly((fields ?? []).Select(CloneField).ToArray());
        Description = description ?? string.Empty;
        DescriptionFormat = descriptionFormat;
        Tags = Freeze(tags);
        Relations = Freeze(relations);
        Comments = Freeze(comments);
        Attachments = Array.AsReadOnly((attachments ?? []).Select(CloneAttachment).ToArray());
        ChangedAtUtc = changedAtUtc?.ToUniversalTime();
    }

    public string WorkItemId { get; }
    public string Revision { get; }
    public string WorkItemType { get; }
    public string Title { get; }
    public IReadOnlyList<RawWorkItemField> Fields { get; }
    public string Description { get; }
    public WorkItemContentFormat DescriptionFormat { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<RawWorkItemRelation> Relations { get; }
    public IReadOnlyList<RawWorkItemComment> Comments { get; }
    public IReadOnlyList<RawAttachmentMetadata> Attachments { get; }
    /// <summary>Optional provider-neutral last-change observation, used only for first-run lookback selection.</summary>
    public DateTimeOffset? ChangedAtUtc { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values) =>
        Array.AsReadOnly((values ?? []).ToArray());

    private static RawWorkItemField CloneField(RawWorkItemField field) =>
        field with { Value = field.Value.ValueKind == JsonValueKind.Undefined ? default : field.Value.Clone() };

    private static RawAttachmentMetadata CloneAttachment(RawAttachmentMetadata attachment) =>
        attachment with { ContentBytes = attachment.ContentBytes?.ToArray() };
}

public sealed record RawWorkItemField(
    string ReferenceName,
    string? DisplayName,
    JsonElement Value,
    WorkItemContentFormat Format = WorkItemContentFormat.PlainText);

public sealed record RawWorkItemComment(
    string Id,
    string? AuthorDisplayName,
    DateTimeOffset? CreatedAt,
    string Content,
    WorkItemContentFormat Format = WorkItemContentFormat.PlainText);

public sealed record RawWorkItemRelation(
    string Reference,
    string RelationType,
    string? DisplayName = null);

public sealed record RawAttachmentMetadata(
    string Reference,
    string FileName,
    string? ContentType = null,
    long? SizeBytes = null,
    byte[]? ContentBytes = null,
    [property: JsonIgnore] IAttachmentContentSource? Content = null);

/// <summary>
/// Provider-neutral transient attachment content. Future work-item adapters may implement this
/// without exposing their SDK, HTTP response, or filesystem types to Application.
/// </summary>
public interface IAttachmentContentSource
{
    long? Length { get; }
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

public sealed class AttachmentContentUnavailableException(string safeCategory)
    : IOException("Attachment content is unavailable.")
{
    public string SafeCategory { get; } = safeCategory;
}

/// <summary>Defensively copied content intended for deterministic tests and bounded adapters.</summary>
public sealed class BufferedAttachmentContent : IAttachmentContentSource
{
    private readonly byte[] content;

    public BufferedAttachmentContent(ReadOnlySpan<byte> content) => this.content = content.ToArray();
    public long? Length => content.LongLength;
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}

public enum WorkItemContentFormat
{
    PlainText,
    Html,
    Markdown,
    Json
}

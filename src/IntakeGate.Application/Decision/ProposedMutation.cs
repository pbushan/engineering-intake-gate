namespace IntakeGate.Application.Decision;

public enum ProposedMutationType
{
    RemoveTag,
    AddTag,
    PostComment
}

/// <summary>The complete mutation vocabulary. It represents intent, never an attempted write.</summary>
public sealed record ProposedMutation
{
    public ProposedMutation(ProposedMutationType type, string? tag, string? body, string? commentMarker)
    {
        if (type is ProposedMutationType.AddTag or ProposedMutationType.RemoveTag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            if (body is not null || commentMarker is not null)
            {
                throw new ArgumentException("Tag mutations cannot contain comment data.");
            }
        }
        else if (type == ProposedMutationType.PostComment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body);
            ArgumentException.ThrowIfNullOrWhiteSpace(commentMarker);
            if (tag is not null)
            {
                throw new ArgumentException("Comment mutations cannot contain a tag.");
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        Tag = tag;
        Body = body;
        CommentMarker = commentMarker;
    }

    public ProposedMutationType Type { get; }
    public string? Tag { get; }
    public string? Body { get; }
    public string? CommentMarker { get; }

    public static ProposedMutation AddTag(string tag) => new(ProposedMutationType.AddTag, tag, null, null);
    public static ProposedMutation RemoveTag(string tag) => new(ProposedMutationType.RemoveTag, tag, null, null);
    public static ProposedMutation PostComment(string body, string marker) => new(ProposedMutationType.PostComment, null, body, marker);
}

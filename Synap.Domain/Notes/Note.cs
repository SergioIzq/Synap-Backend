using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

[Table("notes")]
public sealed class Note : AbsEntity<NoteId>
{
    private readonly List<Tag> _tags = [];

    private Note() : base(NoteId.Create(Guid.NewGuid()).Value)
    {
    }

    private Note(NoteId id, UserId userId, NoteType type, string? title, string content) : base(id)
    {
        UserId = userId;
        Type = type;
        Title = title;
        Content = content;
        UpdatedAt = FechaCreacion;
    }

    public UserId UserId { get; private set; }
    public NoteType Type { get; private set; }
    public string? Title { get; private set; }
    public string Content { get; private set; } = null!;
    public DateTime UpdatedAt { get; private set; }
    public NoteMetadata? Metadata { get; private set; }

    public IReadOnlyCollection<Tag> Tags => _tags.AsReadOnly();

    /// <summary>
    /// Content is stored exactly as submitted - callers must not trim/reformat it, so code
    /// snippets keep their whitespace and indentation (specs/knowledge-vault).
    /// </summary>
    public static Note Create(UserId userId, NoteType type, string? title, string content)
        => new(NoteId.Create(Guid.NewGuid()).Value, userId, type, title, content);

    public void UpdateContent(string? title, string content)
    {
        Title = title;
        Content = content;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddTag(Tag tag)
    {
        if (_tags.Any(t => t.Id.Value == tag.Id.Value))
        {
            return;
        }

        _tags.Add(tag);
    }

    /// <summary>Attaches (or replaces) the metadata scraped from a bookmark's linked page.</summary>
    public void AttachMetadata(NoteMetadata metadata)
    {
        Metadata = metadata;
    }
}

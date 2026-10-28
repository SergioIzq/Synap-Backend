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

    private Note(NoteId id, UserId userId, NoteType type, string? title, string content, NoteStatus? status) : base(id)
    {
        UserId = userId;
        Type = type;
        Title = title;
        Content = content;
        UpdatedAt = FechaCreacion;
        Status = status;
        StatusChangedAt = status is null ? null : FechaCreacion;
    }

    public UserId UserId { get; private set; }
    public NoteType Type { get; private set; }
    public string? Title { get; private set; }
    public string Content { get; private set; } = null!;
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Cuándo el dueño abrió el detalle de esta nota por última vez desde la web. Null mientras
    /// no se haya registrado ninguna apertura (smart-resurfacing, Decisión 4). No lo mueve nada
    /// salvo <see cref="MarkViewed"/>.
    /// </summary>
    public DateTime? LastViewedAt { get; private set; }

    /// <summary>
    /// Cuándo esta nota apareció por última vez en la sección «Para retomar» de un briefing
    /// entregado. Null mientras no haya sido rescatada (smart-resurfacing, Decisión 6).
    /// </summary>
    public DateTime? LastResurfacedAt { get; private set; }

    /// <summary>
    /// What this note is as work, or null when it is material rather than work - a bookmark, a
    /// snippet, a captured idea (note-status design.md Decision 1). Null is the default and is
    /// never treated as a kind of work.
    /// </summary>
    public NoteStatus? Status { get; private set; }

    /// <summary>
    /// When <see cref="Status"/> last changed, or null while the note has never had one. Kept
    /// apart from <see cref="UpdatedAt"/> on purpose: that one answers "when did the text last
    /// change", this one "how long has it stood in this state" - the question the briefing asks of
    /// a paused note (design.md Decision 5).
    /// </summary>
    public DateTime? StatusChangedAt { get; private set; }

    public NoteMetadata? Metadata { get; private set; }

    public IReadOnlyCollection<Tag> Tags => _tags.AsReadOnly();

    /// <summary>
    /// Content is stored exactly as submitted - callers must not trim/reformat it, so code
    /// snippets keep their whitespace and indentation (specs/knowledge-vault).
    /// </summary>
    public static Note Create(UserId userId, NoteType type, string? title, string content, NoteStatus? status = null)
        => new(NoteId.Create(Guid.NewGuid()).Value, userId, type, title, content, status);

    /// <summary>
    /// Only the text. <see cref="Status"/> and <see cref="StatusChangedAt"/> are deliberately not
    /// touched here (specs/knowledge-vault "Editing content leaves the status alone").
    /// </summary>
    public void UpdateContent(string? title, string content)
    {
        Title = title;
        Content = content;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Sets, replaces or clears the status, recording the moment. Accepts any status from any
    /// other, and null to clear: the four statuses are there to be queried, not to constrain the
    /// order a user works in, so there is nothing here to reject (design.md Decision 3).
    ///
    /// <see cref="UpdatedAt"/> is untouched - marking a note done is not editing it, and moving it
    /// would destroy the "untouched for N days" signal the briefing reads (design.md Decision 5).
    /// </summary>
    public void SetStatus(NoteStatus? status)
    {
        Status = status;
        StatusChangedAt = status is null ? null : DateTime.UtcNow;
    }

    /// <summary>
    /// Registra que el dueño ha abierto esta nota ahora. Solo mueve <see cref="LastViewedAt"/>;
    /// no toca <see cref="UpdatedAt"/>, <see cref="Status"/> ni <see cref="StatusChangedAt"/>
    /// (smart-resurfacing spec «Abrir no es editar»).
    /// </summary>
    public void MarkViewed()
    {
        LastViewedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks this note as resurfaced now. Called by <c>BriefingDispatcher</c> after a successful
    /// delivery (smart-resurfacing, Decisión 6). Does not touch any other field.
    /// </summary>
    public void MarkResurfaced()
    {
        LastResurfacedAt = DateTime.UtcNow;
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

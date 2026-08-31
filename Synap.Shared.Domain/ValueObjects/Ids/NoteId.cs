using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct NoteId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use NoteId.Create() for validation or NoteId.CreateFromDatabase() from infrastructure.", error: true)]
    public NoteId()
    {
        Value = Guid.Empty;
    }

    public NoteId(Guid value)
    {
        Value = value;
    }

    public static Result<NoteId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<NoteId>(Error.Validation("The note ID cannot be empty."));
        }

        return Result.Success(new NoteId(value));
    }

    public static NoteId CreateFromDatabase(Guid value) => new(value);
}

using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct MemoryEntryId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use MemoryEntryId.Create() for validation or MemoryEntryId.CreateFromDatabase() from infrastructure.", error: true)]
    public MemoryEntryId()
    {
        Value = Guid.Empty;
    }

    public MemoryEntryId(Guid value)
    {
        Value = value;
    }

    public static Result<MemoryEntryId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<MemoryEntryId>(Error.Validation("El identificador del recuerdo no puede estar vacío."));
        }

        return Result.Success(new MemoryEntryId(value));
    }

    public static MemoryEntryId CreateFromDatabase(Guid value) => new(value);
}

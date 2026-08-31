using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct TagId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use TagId.Create() for validation or TagId.CreateFromDatabase() from infrastructure.", error: true)]
    public TagId()
    {
        Value = Guid.Empty;
    }

    public TagId(Guid value)
    {
        Value = value;
    }

    public static Result<TagId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<TagId>(Error.Validation("The tag ID cannot be empty."));
        }

        return Result.Success(new TagId(value));
    }

    public static TagId CreateFromDatabase(Guid value) => new(value);
}

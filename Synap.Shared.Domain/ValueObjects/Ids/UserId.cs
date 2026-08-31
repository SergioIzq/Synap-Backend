using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct UserId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use UserId.Create() for validation or UserId.CreateFromDatabase() from infrastructure.", error: true)]
    public UserId()
    {
        Value = Guid.Empty;
    }

    public UserId(Guid value)
    {
        Value = value;
    }

    public static Result<UserId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<UserId>(Error.Validation("The user ID cannot be empty."));
        }

        return Result.Success(new UserId(value));
    }

    public static UserId CreateFromDatabase(Guid value) => new(value);
}

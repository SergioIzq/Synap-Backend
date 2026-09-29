using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct ReminderId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use ReminderId.Create() for validation or ReminderId.CreateFromDatabase() from infrastructure.", error: true)]
    public ReminderId()
    {
        Value = Guid.Empty;
    }

    public ReminderId(Guid value)
    {
        Value = value;
    }

    public static Result<ReminderId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<ReminderId>(Error.Validation("El identificador del recordatorio no puede estar vacío."));
        }

        return Result.Success(new ReminderId(value));
    }

    public static ReminderId CreateFromDatabase(Guid value) => new(value);
}

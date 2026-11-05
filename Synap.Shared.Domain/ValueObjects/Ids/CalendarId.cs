using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct CalendarId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use CalendarId.Create() for validation or CalendarId.CreateFromDatabase() from infrastructure.", error: true)]
    public CalendarId()
    {
        Value = Guid.Empty;
    }

    public CalendarId(Guid value)
    {
        Value = value;
    }

    public static Result<CalendarId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<CalendarId>(Error.Validation("El identificador del calendario no puede estar vacío."));
        }

        return Result.Success(new CalendarId(value));
    }

    public static CalendarId CreateFromDatabase(Guid value) => new(value);
}

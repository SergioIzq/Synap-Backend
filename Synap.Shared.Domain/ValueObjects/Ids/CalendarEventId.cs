using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;

namespace Synap.Shared.Domain.ValueObjects.Ids;

public readonly record struct CalendarEventId : IGuidValueObject
{
    public Guid Value { get; init; }

    [Obsolete("Use CalendarEventId.Create() for validation or CalendarEventId.CreateFromDatabase() from infrastructure.", error: true)]
    public CalendarEventId()
    {
        Value = Guid.Empty;
    }

    public CalendarEventId(Guid value)
    {
        Value = value;
    }

    public static Result<CalendarEventId> Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            return Result.Failure<CalendarEventId>(Error.Validation("El identificador del evento no puede estar vacío."));
        }

        return Result.Success(new CalendarEventId(value));
    }

    public static CalendarEventId CreateFromDatabase(Guid value) => new(value);
}

using SergioIzq.Domain.Kernel.Abstractions.Results;
using System.Text.RegularExpressions;

namespace Synap.Shared.Domain.ValueObjects;

public readonly record struct Email
{
    private static readonly Regex EmailRegex =
        new(@"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Value { get; }

    [Obsolete("Use Email.Create() for validation or Email.CreateFromDatabase() from infrastructure.", error: true)]
    public Email()
    {
        Value = string.Empty;
    }

    private Email(string value)
    {
        Value = value;
    }

    public static Result<Email> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<Email>(Error.Validation("El email no puede estar vacío."));
        }

        if (!EmailRegex.IsMatch(value))
        {
            return Result.Failure<Email>(Error.Validation($"'{value}' no es una dirección de email válida."));
        }

        return Result.Success(new Email(value.ToLowerInvariant()));
    }

    public static Email CreateFromDatabase(string value) => new(value.ToLowerInvariant());
}

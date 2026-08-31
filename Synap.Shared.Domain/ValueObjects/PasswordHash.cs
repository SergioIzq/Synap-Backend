using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Shared.Domain.ValueObjects;

public readonly record struct PasswordHash
{
    public string Value { get; }

    [Obsolete("Use PasswordHash.Create() for validation or PasswordHash.CreateFromDatabase() from infrastructure.", error: true)]
    public PasswordHash()
    {
        Value = string.Empty;
    }

    private PasswordHash(string value)
    {
        Value = value;
    }

    public static Result<PasswordHash> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 10)
        {
            return Result.Failure<PasswordHash>(Error.Validation("The provided password hash is invalid or empty."));
        }

        return Result.Success(new PasswordHash(value));
    }

    public static PasswordHash CreateFromDatabase(string value) => new(value);
}

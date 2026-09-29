using SergioIzq.Domain.Kernel.Abstractions;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

/// <summary>
/// A fact about the user that the assistant takes into account in every answer
/// (specs/assistant-memory). Deliberately small and bounded, so the whole memory is always sent
/// with each question instead of being retrieved (assistant-agent-foundations design.md Decision 8).
/// </summary>
[Table("memory_entries")]
public sealed class MemoryEntry : AbsEntity<MemoryEntryId>
{
    public const int MaxTextLength = 200;
    public const int MaxEntriesPerUser = 25;

    public static readonly Error EmptyText = Error.Validation("El recuerdo no puede estar vacío.");
    public static readonly Error TextTooLong = Error.Validation($"El recuerdo no puede superar los {MaxTextLength} caracteres.");
    public static readonly Error MemoryFull = Error.Validation(
        $"La memoria está llena ({MaxEntriesPerUser} recuerdos). Borra alguno desde Configuración antes de añadir otro.");
    public static readonly Error NotFound = Error.NotFound("Recuerdo no encontrado.");

    private MemoryEntry() : base(MemoryEntryId.Create(Guid.NewGuid()).Value)
    {
    }

    private MemoryEntry(MemoryEntryId id, UserId userId, string text) : base(id)
    {
        UserId = userId;
        Text = text;
        // Its own UTC clock rather than FechaCreacion's: the kernel stamps that with local time,
        // and Edit's UtcNow must be comparable for "most recently updated first".
        UpdatedAt = DateTime.UtcNow;
    }

    public UserId UserId { get; private set; }
    public string Text { get; private set; } = null!;
    public DateTime UpdatedAt { get; private set; }

    public static Result<MemoryEntry> Create(UserId userId, string? text)
    {
        var validated = Validate(text);
        return validated.IsFailure
            ? Result.Failure<MemoryEntry>(validated.Error)
            : Result.Success(new MemoryEntry(MemoryEntryId.Create(Guid.NewGuid()).Value, userId, validated.Value));
    }

    public Result Edit(string? text)
    {
        var validated = Validate(text);
        if (validated.IsFailure)
        {
            return Result.Failure(validated.Error);
        }

        Text = validated.Value;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Trimmed text, or why it can't be stored.</summary>
    private static Result<string> Validate(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Result.Failure<string>(EmptyText);
        }

        return trimmed.Length > MaxTextLength ? Result.Failure<string>(TextTooLong) : Result.Success(trimmed);
    }
}

using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Application.Features.Notes;

/// <summary>
/// "Get or create the user's tag" - shared by AddTag and CreateNote so both reuse an existing
/// tag the same way (specs/knowledge-vault "Tagging").
/// </summary>
internal static class TagAssignment
{
    public const int MaxTagsPerNote = 10;

    public static readonly Error EmptyTag = Error.Validation("El nombre de la etiqueta no puede estar vacío.");
    public static readonly Error TooManyTags = Error.Validation($"Una nota puede tener como máximo {MaxTagsPerNote} etiquetas.");

    /// <summary>Trims, drops case-insensitive duplicates (first spelling wins) and validates.</summary>
    public static Result<IReadOnlyList<string>> Normalize(IEnumerable<string?>? rawNames)
    {
        var names = new List<string>();
        foreach (var raw in rawNames ?? [])
        {
            var name = raw?.Trim().TrimStart('#').Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                return Result.Failure<IReadOnlyList<string>>(EmptyTag);
            }

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names.Count > MaxTagsPerNote
            ? Result.Failure<IReadOnlyList<string>>(TooManyTags)
            : Result.Success<IReadOnlyList<string>>(names);
    }

    /// <summary>Tracked tag with this exact name for the user, created (unsaved) if missing.</summary>
    public static async Task<Tag> GetOrCreateAsync(
        ITagWriteRepository tagWriteRepository, Guid userId, string name, CancellationToken cancellationToken)
    {
        var tag = await tagWriteRepository.GetByNameAsync(userId, name, cancellationToken);
        if (tag is null)
        {
            tag = Tag.Create(UserId.CreateFromDatabase(userId), name);
            await tagWriteRepository.CreateAsync(tag, cancellationToken);
        }

        return tag;
    }
}

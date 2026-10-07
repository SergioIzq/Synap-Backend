namespace Synap.Domain;

/// <summary>
/// Which notes a status filter asks for: a set of statuses, and separately whether notes carrying
/// no status at all are wanted (note-status design.md Decision 4).
///
/// Two fields rather than one set with a magic member, because "no status" is the absence of a
/// value in the database and has to be asked for with IS NULL, not with an equality - the same
/// reason <see cref="NoteStatus"/> has no None member.
/// </summary>
public sealed record NoteStatusFilter(IReadOnlySet<NoteStatus> Statuses, bool IncludeWithoutStatus)
{
    /// <summary>The wire value standing for notes that carry no status.</summary>
    public const string WithoutStatusValue = "none";

    /// <summary>
    /// What a request naming no status filter means: everything live - every status except
    /// completed, and notes with no status, which are most of a vault. Completed is the only
    /// status whose whole point is being done (design.md Decision 4).
    /// </summary>
    public static readonly NoteStatusFilter Default =
        new(new HashSet<NoteStatus> { NoteStatus.Pending, NoteStatus.InProgress, NoteStatus.Paused }, IncludeWithoutStatus: true);

    /// <summary>Only the notes that are not work at all - the reference library.</summary>
    public static readonly NoteStatusFilter WithoutStatus =
        new(new HashSet<NoteStatus>(), IncludeWithoutStatus: true);

    public static NoteStatusFilter Of(params NoteStatus[] statuses)
        => Of(statuses, includeWithoutStatus: false);

    public static NoteStatusFilter Of(IEnumerable<NoteStatus> statuses, bool includeWithoutStatus)
        => new(statuses.ToHashSet(), includeWithoutStatus);

    /// <summary>
    /// True when the filter asks for every status and for the unmarked notes too, so the query can
    /// skip the condition entirely rather than listing all four.
    /// </summary>
    public bool MatchesEverything => IncludeWithoutStatus && Statuses.Count == Enum.GetValues<NoteStatus>().Length;

    /// <summary>
    /// The accepted wire values, for the validation message - the four statuses in camelCase plus
    /// <see cref="WithoutStatusValue"/>.
    /// </summary>
    public static IReadOnlyList<string> AcceptedValues { get; } =
        [.. Enum.GetNames<NoteStatus>().Select(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName), WithoutStatusValue];

    /// <summary>
    /// Parses a comma-separated wire filter ("pending,inProgress", "none"). Null or blank yields
    /// <see cref="Default"/>. Returns false on any value that is neither one of the four statuses
    /// nor <see cref="WithoutStatusValue"/>, so the caller can reject the whole request rather than
    /// silently returning the wrong notes. Matching is case-insensitive, like NoteType's.
    /// </summary>
    public static bool TryParse(string? value, out NoteStatusFilter filter)
    {
        filter = Default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var statuses = new HashSet<NoteStatus>();
        var includeWithoutStatus = false;

        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, WithoutStatusValue, StringComparison.OrdinalIgnoreCase))
            {
                includeWithoutStatus = true;
                continue;
            }

            // Names only - Enum.TryParse would also accept numbers like "7", as
            // SearchNotesQueryHandler's note-type validation already notes.
            if (Enum.GetNames<NoteStatus>().FirstOrDefault(n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase)) is not { } name)
            {
                return false;
            }

            statuses.Add(Enum.Parse<NoteStatus>(name));
        }

        // Only separators, e.g. ",,": nothing was named, so nothing was asked for.
        if (statuses.Count == 0 && !includeWithoutStatus)
        {
            return false;
        }

        filter = new NoteStatusFilter(statuses, includeWithoutStatus);
        return true;
    }
}

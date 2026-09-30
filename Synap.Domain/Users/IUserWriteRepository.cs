using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface IUserWriteRepository : IWriteRepository<User, UserId>
{
    /// <summary>
    /// Permanently deletes the user and everything they own (notes - and with them tags links
    /// and embeddings - tags, Groq key, API token) in one transaction (specs/identity "Delete
    /// account"). notes/tags have no FK to users, so this can't rely on a cascade.
    /// </summary>
    Task DeleteWithAllDataAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracked, for the briefing sweep: the users that could be owed a briefing right now - the
    /// setting on, and a local day not settled yet (daily-briefing design.md Decision 3). Whether
    /// the chosen hour has actually come round where each of them is stays with
    /// <see cref="User.IsBriefingDue"/>, because only the user knows their own timezone; this
    /// narrows the table down to the few rows worth asking about.
    ///
    /// <paramref name="maxLocalDate"/> is the furthest ahead any timezone can be, so a user whose
    /// day is already settled is left out without risking a false negative.
    /// </summary>
    Task<IReadOnlyList<User>> ListBriefingCandidatesAsync(
        DateOnly maxLocalDate, int limit, CancellationToken cancellationToken = default);
}

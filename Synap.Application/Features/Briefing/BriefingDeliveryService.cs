using Microsoft.Extensions.Logging;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Reminders;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// One sweep of the morning briefing (daily-briefing design.md Decision 2): the users whose
/// chosen hour has come round on their own clock are briefed, once for that local day.
///
/// The day is recorded as resolved when a briefing goes out and when there was nothing to report,
/// and deliberately not when delivery failed - that is what makes the next sweep try again while
/// the day lasts (Decision 3).
/// </summary>
public sealed class BriefingDeliveryService
{
    /// <summary>A ceiling per sweep, so one tick can never become unbounded work.</summary>
    public const int MaxPerSweep = 200;

    private readonly IUserWriteRepository _userWriteRepository;
    private readonly BriefingDispatcher _dispatcher;
    private readonly ITelegramSender _telegramSender;
    private readonly IUnitOfWork _unitOfWork;
    private readonly WithheldBriefingRecorder _withheld;
    private readonly ILogger<BriefingDeliveryService> _logger;

    public BriefingDeliveryService(
        IUserWriteRepository userWriteRepository,
        BriefingDispatcher dispatcher,
        ITelegramSender telegramSender,
        IUnitOfWork unitOfWork,
        WithheldBriefingRecorder withheld,
        ILogger<BriefingDeliveryService> logger)
    {
        _userWriteRepository = userWriteRepository;
        _dispatcher = dispatcher;
        _telegramSender = telegramSender;
        _unitOfWork = unitOfWork;
        _withheld = withheld;
        _logger = logger;
    }

    /// <summary>Briefs everyone owed one at this instant; returns how many messages went out.</summary>
    public async Task<int> SweepAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // Nothing is sent and no day is resolved with delivery turned off for the deployment, so
        // turning it on later still briefs people (specs/briefing "Delivery turned off").
        if (!_telegramSender.IsEnabled)
        {
            return 0;
        }

        var candidates = await _userWriteRepository.ListBriefingCandidatesAsync(
            FurthestLocalDate(nowUtc), MaxPerSweep, cancellationToken);

        var sent = 0;
        var changed = 0;

        foreach (var user in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await BriefAsync(user, nowUtc, cancellationToken);
                sent += outcome.Sent ? 1 : 0;
                changed += outcome.Resolved ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One user's briefing must not cost everyone else theirs (specs/briefing "One
                // failure does not stop the rest"). The day stays unresolved, so it is retried.
                _logger.LogError(exception, "The briefing of user {UserId} failed; the others are unaffected", user.Id.Value);
            }
        }

        if (changed > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }

    private async Task<(bool Sent, bool Resolved)> BriefAsync(User user, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!user.IsBriefingDue(nowUtc))
        {
            return (false, false);
        }

        var localDay = DateOnly.FromDateTime(UserClock.ToLocal(nowUtc, user.Timezone));

        // answerWhenEmpty: false - nobody asked for this one, so a day with nothing to report
        // passes in silence (specs/briefing "Nothing to report means nothing is sent unasked").
        var result = await _dispatcher.SendAsync(user, nowUtc, answerWhenEmpty: false, cancellationToken);

        switch (result)
        {
            case BriefingDispatchResult.NoChatLinked:
                // Said once per user, not every sweep - the condition is a configuration state, so
                // repeating it would only drown the log (design.md Decision 6). The day is left
                // unresolved so linking a chat later today still gets them a briefing.
                if (_withheld.ShouldRecord(user.Id.Value))
                {
                    _logger.LogWarning(
                        "The briefing of user {UserId} was withheld: the user has no linked Telegram chat", user.Id.Value);
                }

                return (false, false);

            case BriefingDispatchResult.DeliveryFailed:
                // Left unresolved on purpose: that is what makes the next sweep try again today.
                _logger.LogWarning(
                    "The briefing of user {UserId} could not be delivered; it will be tried again today", user.Id.Value);
                return (false, false);

            case BriefingDispatchResult.NothingToReport:
                // Settled without a message, so the user is not pestered and the sweep does not
                // reconsider them every quarter of an hour until midnight ("Empty day").
                user.MarkBriefingResolved(localDay);
                _userWriteRepository.Update(user);
                return (false, true);

            default:
                user.MarkBriefingResolved(localDay);
                _userWriteRepository.Update(user);
                _withheld.Forget(user.Id.Value);
                return (true, true);
        }
    }

    /// <summary>
    /// The furthest ahead any timezone runs (UTC+14), so the query can drop the users whose day is
    /// already settled without ever dropping one whose day has not started here yet.
    /// </summary>
    private static DateOnly FurthestLocalDate(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc.AddHours(14));
}

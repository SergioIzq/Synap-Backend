using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Briefing;

/// <summary>What came of trying to send one user their briefing.</summary>
public enum BriefingDispatchResult
{
    Sent,

    /// <summary>Nothing in any section, and the caller did not want an answer saying so.</summary>
    NothingToReport,

    /// <summary>Telegram is the only channel and this user has not linked a chat.</summary>
    NoChatLinked,

    /// <summary>The message was formed and the send failed.</summary>
    DeliveryFailed,
}

/// <summary>
/// Builds one user's briefing and sends it to their chat. The single place that does it, shared by
/// the unattended sweep and by the two ways a user can ask for one (daily-briefing design.md
/// Decision 7) - so a briefing asked for is the same briefing that would have arrived.
///
/// It decides nothing about the resolved day: that belongs to the sweep, because asking for a
/// briefing must not consume the automatic one.
/// </summary>
public sealed class BriefingDispatcher
{
    private readonly BriefingContentService _content;
    private readonly ITelegramSender _telegramSender;

    public BriefingDispatcher(BriefingContentService content, ITelegramSender telegramSender)
    {
        _content = content;
        _telegramSender = telegramSender;
    }

    /// <summary>
    /// <paramref name="answerWhenEmpty"/> is the one difference between the two callers. The sweep
    /// passes false and stays silent on a day with nothing to report; a user who asked gets a
    /// message saying so, because a request that produces silence is indistinguishable from one
    /// that failed (specs/briefing "Asked for with nothing to report").
    /// </summary>
    public async Task<BriefingDispatchResult> SendAsync(
        User user, DateTime nowUtc, bool answerWhenEmpty, CancellationToken cancellationToken = default)
    {
        if (!user.HasTelegram)
        {
            return BriefingDispatchResult.NoChatLinked;
        }

        var content = await _content.BuildAsync(user.Id.Value, nowUtc, user.Timezone, cancellationToken);
        var text = BriefingMessage.Text(content, user.Timezone);

        if (text is null)
        {
            if (!answerWhenEmpty)
            {
                return BriefingDispatchResult.NothingToReport;
            }

            text = BriefingMessage.NothingToReport;
        }

        return await _telegramSender.SendAsync(new TelegramMessage(user.TelegramChatId!, text), cancellationToken)
            ? BriefingDispatchResult.Sent
            : BriefingDispatchResult.DeliveryFailed;
    }
}

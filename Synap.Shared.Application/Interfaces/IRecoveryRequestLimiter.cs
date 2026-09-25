namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Caps password-recovery emails per address (specs/identity "Too many recovery requests for
/// one email") - independent of the per-IP rate limit, so rotating IPs can't flood a mailbox.
/// </summary>
public interface IRecoveryRequestLimiter
{
    /// <summary>True if another email may be sent to this (normalized) address now.</summary>
    bool TryAcquire(string normalizedEmail);
}

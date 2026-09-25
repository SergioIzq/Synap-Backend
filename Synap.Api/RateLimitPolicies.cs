namespace Synap.Api;

public static class RateLimitPolicies
{
    /// <summary>Applied to quick-capture and the assistant chat - see Program.cs for the policy definition.</summary>
    public const string AiHeavy = "ai-heavy";

    /// <summary>Login attempts per client IP (backend-hardening design.md Decision 3).</summary>
    public const string Auth = "auth";

    /// <summary>Registrations per client IP.</summary>
    public const string Register = "register";

    public const int AuthPermitLimit = 10;
    public static readonly TimeSpan AuthWindow = TimeSpan.FromMinutes(1);

    public const int RegisterPermitLimit = 5;
    public static readonly TimeSpan RegisterWindow = TimeSpan.FromHours(1);

    public const string RejectionMessage = "Demasiados intentos. Espera un momento y vuelve a intentarlo.";
}

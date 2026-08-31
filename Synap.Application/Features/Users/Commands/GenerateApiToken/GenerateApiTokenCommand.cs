using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Users.Commands.GenerateApiToken;

/// <summary>
/// Generates (or regenerates) the authenticated user's personal access token, used for
/// non-interactive requests such as the iOS Shortcut's quick-capture call. The plaintext value
/// is only ever returned here; regenerating invalidates the previous token immediately.
/// </summary>
public sealed record GenerateApiTokenCommand(Guid UserId) : ICommand<string>;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Synap.Domain;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Synap.Shared.Application.Interfaces;

namespace Synap.Api.Authentication;

public sealed class ApiTokenAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates requests carrying the user's personal access token (see specs/identity and
/// design.md Decision 3 - the iOS Shortcut's quick-capture call can't rely on the short-lived
/// session JWT). Emits the same claim shape as the kernel's JWT generator so
/// AbsController.GetCurrentUserId() works unmodified for either scheme. Ported from Kash's
/// token-personal-api feature.
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<ApiTokenAuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly IUserReadRepository _userReadRepository;
    private readonly IApiTokenHasher _apiTokenHasher;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<ApiTokenAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IUserReadRepository userReadRepository,
        IApiTokenHasher apiTokenHasher)
        : base(options, logger, encoder)
    {
        _userReadRepository = userReadRepository;
        _apiTokenHasher = apiTokenHasher;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authHeader[BearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = _apiTokenHasher.Hash(token);
        var user = await _userReadRepository.GetByApiTokenHashAsync(hash, Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid API token.");
        }

        var claims = new[]
        {
            new Claim("sub", user.Id.Value.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.Value.ToString()),
            new Claim("email", user.Email.Value),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

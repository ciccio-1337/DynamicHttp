using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sample;

// Dev-only authentication handler: authenticates any request that presents the X-Dev-User
// header and attaches a users.read claim plus the "Admin" role so the sample's
// [DynamicAuthorize] policy and Roles attributes can be exercised without a real identity
// provider.
//
//   curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5
//
// Requests without the header fall through to the default 401 challenge.
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Dev";
    private const string HeaderName = "X-Dev-User";
    private const string PolicyClaimType = "users.read";

    #pragma warning disable CS0618 // The current shared framework still only exposes the ISystemClock ctor.
    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? user = Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Missing '{HeaderName}' header."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(PolicyClaimType, "true")
            ],
            Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
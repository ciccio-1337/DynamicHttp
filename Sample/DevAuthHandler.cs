using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sample;

// Dev-only authentication handler: authenticates any loopback request that presents the
// X-Dev-User header. It is registered only when ASPNETCORE_ENVIRONMENT=Development AND
// DevAuth:Enabled=true. The Admin role is opt-in via DevAuth:GrantAdmin=true; remote addresses
// require DevAuth:AllowRemote=true.
//
//   dotnet run --project Sample -- --DevAuth:Enabled=true
//   curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5
//
// Requests without the header fall through to the default 401 challenge.
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Dev";
    private const string HeaderName = "X-Dev-User";
    private const string PolicyClaimType = "users.read";

    private readonly bool _grantAdmin;
    private readonly bool _allowRemote;

    #pragma warning disable CS0618 // The current shared framework still only exposes the ISystemClock ctor.
    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, IConfiguration configuration)
        : base(options, logger, encoder, clock)
    {
        _grantAdmin = configuration.GetValue("DevAuth:GrantAdmin", false);
        _allowRemote = configuration.GetValue("DevAuth:AllowRemote", false);
    }
    #pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        IPAddress? remote = Context.Connection.RemoteIpAddress;

        if (!_allowRemote && remote is not null && !IPAddress.IsLoopback(remote))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Development authentication is restricted to loopback requests."));
        }

        string? user = Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Missing '{HeaderName}' header."));
        }

        List<Claim> claims =
        [
            new(ClaimTypes.Name, user),
            new(PolicyClaimType, "true")
        ];

        if (_grantAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

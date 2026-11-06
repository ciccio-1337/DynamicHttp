using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sample;

public sealed class DevAuthOptions : AuthenticationSchemeOptions
{
    public bool Enabled { get; set; }
}

// Dev-only authentication handler: authenticates any loopback request that presents the
// X-Dev-User header. It is enabled only when ASPNETCORE_ENVIRONMENT=Development AND
// DevAuth:Enabled=true. The Admin role is opt-in via DevAuth:GrantAdmin=true; remote addresses
// require DevAuth:AllowRemote=true.
//
//   dotnet run --project Sample -- --DevAuth:Enabled=true
//   curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5
//
// When disabled the handler returns NoResult, so the scheme still provides a default 401
// challenge instead of failing with "No authenticationScheme was specified".
public sealed class DevAuthHandler : AuthenticationHandler<DevAuthOptions>
{
    public const string SchemeName = "Dev";
    private const string HeaderName = "X-Dev-User";
    private const string PolicyClaimType = "users.read";

    private readonly bool _grantAdmin;
    private readonly bool _allowRemote;

    public DevAuthHandler(IOptionsMonitor<DevAuthOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _grantAdmin = configuration.GetValue("DevAuth:GrantAdmin", false);
        _allowRemote = configuration.GetValue("DevAuth:AllowRemote", false);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Enabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

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

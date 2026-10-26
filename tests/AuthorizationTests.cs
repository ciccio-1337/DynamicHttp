using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task Policy_requires_claim()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("/api/auth/policy"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request("/api/auth/policy", User("mario")))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/auth/policy", User("mario"), Permission("read")))).StatusCode);
    }

    [Fact]
    public async Task Policy_and_roles_are_enforced_together()
    {
        // Regression: the Policy branch used to create an AuthorizeAttribute without Roles, so a
        // user who satisfied the policy but lacked the role was allowed through.
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request("/api/auth/policy-role", User("mario"), Permission("read")))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/auth/policy-role",
                User("mario"),
                Permission("read"),
                Role("Admin")))).StatusCode);
    }

    [Fact]
    public async Task Roles_require_authentication_and_membership()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("/api/auth/role"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request("/api/auth/role", User("mario")))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/auth/role", User("mario"), Role("Admin")))).StatusCode);
    }

    [Fact]
    public async Task Explicit_authentication_scheme_is_used()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        // The default scheme rejects every request; only the explicit "Test" scheme succeeds.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("/api/auth/scheme"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/auth/scheme", User("mario")))).StatusCode);
    }

    [Fact]
    public async Task Class_level_policy_applies_to_methods()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request("/api/auth/inherited", User("mario")))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/auth/inherited", User("mario"), Permission("read")))).StatusCode);
    }

    [Fact]
    public async Task Allow_anonymous_on_method_overrides_class_policy()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/auth/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"public\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Framework_authorize_attribute_is_enforced()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Request("/api/framework-auth/protected"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request("/api/framework-auth/protected", User("mario")))).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/framework-auth/protected",
                User("mario"),
                Permission("read")))).StatusCode);
    }

    [Fact]
    public async Task Framework_allow_anonymous_overrides_class_policy()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/framework-auth/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"public\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Require_authorization_by_default_requires_authentication()
    {
        await using WebApplication app = await StartAppAsync(
            options => options.RequireAuthorizationByDefault = true,
            TestAuthHandler.SchemeName);
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/values/plain")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Request("/api/values/plain", User("mario")))).StatusCode);

        // [DynamicAllowAnonymous] still opts out of the default requirement.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/auth/public")).StatusCode);
    }

    [Fact]
    public async Task Require_authorization_by_default_requires_authorization_services()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(AuthorizationService).Assembly);
            options.RequireAuthorizationByDefault = true;
        });

        await using WebApplication app = builder.Build();

        Assert.Throws<DynamicHttpConfigurationException>(() => app.MapDynamicHttp());
    }

    private static HttpRequestMessage Request(string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return request;
    }

    private static (string Name, string Value) User(string name) => ("X-Test-User", name);

    private static (string Name, string Value) Role(string role) => ("X-Test-Role", role);

    private static (string Name, string Value) Permission(string permission) => ("X-Test-Permission", permission);

    private static async Task<WebApplication> StartAppAsync(
        Action<DynamicHttpOptions>? configureOptions = null,
        string defaultScheme = RejectingAuthHandler.SchemeName)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(AuthorizationService).Assembly);
            configureOptions?.Invoke(options);
        });
        builder.Services
            .AddAuthentication(defaultScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, RejectingAuthHandler>(RejectingAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("read", policy => policy.RequireClaim("perm", "read"));

        WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDynamicHttp();

        await app.StartAsync();
        return app;
    }
}

[HttpService("/api/auth")]
[DynamicAuthorize(Policy = "read", AuthenticationSchemes = TestAuthHandler.SchemeName)]
public sealed class AuthorizationService
{
    [HttpGet("/inherited")]
    public string Inherited() => "inherited";

    [HttpGet("/public")]
    [DynamicAllowAnonymous]
    public string Public() => "public";

    [HttpGet("/policy-role")]
    [DynamicAuthorize(Policy = "read", Roles = "Admin")]
    public string PolicyRole() => "policy-role";
}

[HttpService("/api/auth")]
[DynamicAuthorize(AuthenticationSchemes = TestAuthHandler.SchemeName)]
public sealed class AuthorizationRulesService
{
    [HttpGet("/policy")]
    [DynamicAuthorize(Policy = "read")]
    public string Policy() => "policy";

    [HttpGet("/role")]
    [DynamicAuthorize(Roles = "Admin")]
    public string Role() => "role";

    [HttpGet("/scheme")]
    [DynamicAuthorize(AuthenticationSchemes = TestAuthHandler.SchemeName)]
    public string Scheme() => "scheme";
}

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string user = Request.Headers["X-Test-User"].ToString();

        if (string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims = [new(ClaimTypes.Name, user)];
        string role = Request.Headers["X-Test-Role"].ToString();

        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        string permission = Request.Headers["X-Test-Permission"].ToString();

        if (!string.IsNullOrEmpty(permission))
        {
            claims.Add(new Claim("perm", permission));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public sealed class RejectingAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Rejecting";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

[HttpService("/api/framework-auth")]
[Authorize(Policy = "read", AuthenticationSchemes = TestAuthHandler.SchemeName)]
public sealed class FrameworkAuthorizationService
{
    [HttpGet("/protected")]
    public string Protected() => "protected";

    [HttpGet("/public")]
    [AllowAnonymous]
    public string Public() => "public";
}

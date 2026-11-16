using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sample;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class DevAuthTests
{
    [Fact]
    public async Task Handler_is_disabled_outside_development()
    {
        var result = await AuthenticateAsync(development: false);

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Handler_is_disabled_when_the_option_is_off()
    {
        var result = await AuthenticateAsync(enabled: false);

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Loopback_request_in_development_is_authenticated()
    {
        var result = await AuthenticateAsync(user: "mario");

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal("mario", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("true", principal.FindFirst("users.read")?.Value);
        Assert.DoesNotContain(principal.Claims, claim => claim.Type == ClaimTypes.Role);
    }

    [Fact]
    public async Task Admin_role_is_opt_in()
    {
        var result = await AuthenticateAsync(grantAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal("Admin",
            result.Principal!.FindFirst(ClaimTypes.Role)?.Value);
    }

    [Fact]
    public async Task Header_value_is_trimmed()
    {
        var result = await AuthenticateAsync(user: "  mario  ");

        Assert.True(result.Succeeded);
        Assert.Equal("mario",
            result.Principal!.FindFirst(ClaimTypes.Name)?.Value);
    }

    [Fact]
    public async Task Remote_requests_are_rejected_by_default()
    {
        var result = await AuthenticateAsync(remoteIp: "203.0.113.10");

        Assert.False(result.Succeeded);
        Assert.False(result.None);
    }

    [Fact]
    public async Task Remote_requests_can_be_enabled_explicitly()
    {
        var result = await AuthenticateAsync(remoteIp: "203.0.113.10", allowRemote: true);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Requests_without_a_remote_address_are_rejected()
    {
        var result = await AuthenticateAsync(remoteIp: null);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Non_loopback_host_header_is_rejected()
    {
        var result = await AuthenticateAsync(host: "example.com");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Missing_header_is_rejected()
    {
        var result = await AuthenticateAsync(user: null);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Control_characters_in_the_header_are_rejected()
    {
        var result = await AuthenticateAsync(user: "mario\r\nAdmin");

        Assert.False(result.Succeeded);
        Assert.Contains("invalid", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_header_is_rejected()
    {
        var result = await AuthenticateAsync(user: new string('a', 257));

        Assert.False(result.Succeeded);
        Assert.Contains("invalid", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Maximum_length_header_is_accepted()
    {
        var result = await AuthenticateAsync(user: new string('a', 256));

        Assert.True(result.Succeeded);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        bool enabled = true,
        bool development = true,
        bool grantAdmin = false,
        bool allowRemote = false,
        string? remoteIp = "127.0.0.1",
        string host = "localhost",
        string? user = "mario")
    {
        var context = new DefaultHttpContext();

        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        context.Request.Host = new HostString(host);

        if (user is not null)
        {
            context.Request.Headers["X-Dev-User"] = user;
        }

        Dictionary<string, string?> settings = new()
        {
            ["DevAuth:Enabled"] = enabled.ToString(),
            ["DevAuth:GrantAdmin"] = grantAdmin.ToString(),
            ["DevAuth:AllowRemote"] = allowRemote.ToString()
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var handler = new DevAuthHandler(
            new StaticOptionsMonitor<DevAuthOptions>(new DevAuthOptions { Enabled = enabled }),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            configuration,
            new TestHostEnvironment(
                development ? Environments.Development : Environments.Production));

        var scheme = new AuthenticationScheme(
            DevAuthHandler.SchemeName,
            DevAuthHandler.SchemeName,
            typeof(DevAuthHandler));

        await handler.InitializeAsync(scheme, context);

        return await handler.AuthenticateAsync();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "DynamicHttp.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}

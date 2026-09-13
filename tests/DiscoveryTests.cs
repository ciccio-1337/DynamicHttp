using Xunit;

namespace DynamicHttp.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Discovers_and_compiles_endpoint()
    {
        var endpoints = DynamicHttpDiscovery.Build([typeof(TestService).Assembly]);
        var endpoint = Assert.Single(endpoints, x => x.ServiceType == typeof(TestService));

        Assert.Equal("/api/test/{id}", endpoint.Route);
        Assert.Equal("GET", endpoint.HttpMethod);
        Assert.NotNull(endpoint.Invoker);
    }

    [Fact]
    public void Class_allow_anonymous_with_method_authorize_throws()
    {
        var method = typeof(AnonymousClassAuthorizeMethod).GetMethod(nameof(AnonymousClassAuthorizeMethod.Get))!;

        Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateAuthorization(typeof(AnonymousClassAuthorizeMethod), method));
    }

    [Fact]
    public void Authorize_and_allow_anonymous_on_same_method_throws()
    {
        var method = typeof(AuthorizeAndAnonymousMethod).GetMethod(nameof(AuthorizeAndAnonymousMethod.Get))!;

        Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateAuthorization(typeof(AuthorizeAndAnonymousMethod), method));
    }

    [Fact]
    public void Authorize_and_allow_anonymous_on_same_class_throws()
    {
        var method = typeof(AuthorizedAndAnonymousClass).GetMethod(nameof(AuthorizedAndAnonymousClass.Get))!;

        Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateAuthorization(typeof(AuthorizedAndAnonymousClass), method));
    }

    [Fact]
    public void Class_authorize_with_method_allow_anonymous_is_allowed()
    {
        var method = typeof(AuthorizedClassAnonymousMethod).GetMethod(nameof(AuthorizedClassAnonymousMethod.Get))!;

        DynamicHttpDiscovery.ValidateAuthorization(typeof(AuthorizedClassAnonymousMethod), method);
    }

    [Fact]
    public void Plain_authorization_is_allowed()
    {
        var method = typeof(AuthorizedClass).GetMethod(nameof(AuthorizedClass.Get))!;

        DynamicHttpDiscovery.ValidateAuthorization(typeof(AuthorizedClass), method);
    }

    [HttpService("/api/test")]
    public sealed class TestService
    {
        [HttpGet("/{id}")]
        public string Get([FromRoute] int id) => id.ToString();
    }

    [DynamicAllowAnonymous]
    private sealed class AnonymousClassAuthorizeMethod
    {
        [DynamicAuthorize(Policy = "p")]
        public string Get() => "x";
    }

    private sealed class AuthorizeAndAnonymousMethod
    {
        [DynamicAuthorize(Policy = "p")]
        [DynamicAllowAnonymous]
        public string Get() => "x";
    }

    [DynamicAuthorize(Policy = "p")]
    [DynamicAllowAnonymous]
    private sealed class AuthorizedAndAnonymousClass
    {
        public string Get() => "x";
    }

    [DynamicAuthorize(Policy = "p")]
    private sealed class AuthorizedClassAnonymousMethod
    {
        [DynamicAllowAnonymous]
        public string Get() => "x";
    }

    [DynamicAuthorize(Policy = "p")]
    private sealed class AuthorizedClass
    {
        public string Get() => "x";
    }
}

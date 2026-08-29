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
    public void Rejects_duplicate_routes()
    {
        Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.Build([typeof(DuplicateService).Assembly]));
    }

    [HttpService("/api/test")]
    public sealed class TestService
    {
        [HttpGet("/{id}")]
        public string Get([FromRoute] int id) => id.ToString();
    }

    [HttpService("/api/duplicate")]
    public sealed class DuplicateService
    {
        [HttpGet]
        public string A() => "a";

        [HttpGet]
        public string B() => "b";
    }
}

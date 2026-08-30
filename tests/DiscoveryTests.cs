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

    [HttpService("/api/test")]
    public sealed class TestService
    {
        [HttpGet("/{id}")]
        public string Get([FromRoute] int id) => id.ToString();
    }
}

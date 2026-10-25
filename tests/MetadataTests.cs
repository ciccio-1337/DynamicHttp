using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class MetadataTests
{
    [Fact]
    public async Task Metadata_is_applied_to_the_endpoint()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(MetadataService).Assembly));

        await using WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.MapDynamicHttp();
        await app.StartAsync();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == "/api/metadata/{id}");

        ITagsMetadata tags = Assert.IsAssignableFrom<ITagsMetadata>(
            endpoint.Metadata.GetMetadata<ITagsMetadata>());
        Assert.Contains("metadata", tags.Tags);

        IEndpointGroupNameMetadata group = Assert.IsAssignableFrom<IEndpointGroupNameMetadata>(
            endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>());
        Assert.Equal("v1", group.EndpointGroupName);

        var produced = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        Assert.Contains(produced, x => x.StatusCode == 200 && x.Type == typeof(string));
        Assert.Contains(produced, x => x.StatusCode == 404 && (x.Type is null || x.Type == typeof(void)));
    }
}

[HttpService("/api/metadata", Tags = ["metadata"], GroupName = "v1")]
public sealed class MetadataService
{
    [HttpGet("/{id}")]
    [ProducesResponseType(200, typeof(string))]
    [ProducesResponseType(404)]
    public string Get([FromRoute] int id) => id.ToString();
}

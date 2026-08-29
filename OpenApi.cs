namespace DynamicHttp;

public interface IDynamicHttpOpenApiProvider
{
    void Configure(OpenApiContext context);
}

public sealed class OpenApiContext(IReadOnlyList<EndpointDefinition> endpoints)
{
    public IReadOnlyList<EndpointDefinition> Endpoints { get; } = endpoints;
}

public sealed class AspNetCoreOpenApiProvider : IDynamicHttpOpenApiProvider
{
    public void Configure(OpenApiContext context)
    {
        // The generated endpoints expose native ASP.NET Core metadata.
        // The application may use Microsoft.AspNetCore.OpenApi, Swashbuckle,
        // NSwag, or another OpenAPI implementation to consume that metadata.
    }
}

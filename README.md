# DynamicHttp

Production-oriented attribute-driven HTTP endpoints for ASP.NET Core Minimal APIs.

## Design

DynamicHttp performs discovery and expression/delegate compilation once during startup. The request path uses the already-built endpoint metadata and compiled invokers.

It integrates with ASP.NET Core instead of replacing it:

- endpoint routing
- dependency injection
- authentication/authorization
- ProblemDetails
- endpoint metadata
- OpenAPI tooling
- logging

## Example

```csharp
builder.Services.AddDynamicHttp(options =>
{
    options.ScanAssemblies(typeof(UserService).Assembly);
    options.OpenApi.Enabled = true;
});

var app = builder.Build();

app.UseDynamicHttpExceptionHandling();
app.UseAuthentication();
app.UseAuthorization();

app.MapDynamicHttp();

app.Run();
```

```csharp
[HttpService("/api/users", Tags = ["Users"])]
[DynamicAuthorize(Policy = "users.read")]
public sealed class UserService
{
    [HttpGet("/{id}")]
    [ProducesResponseType<UserDto>(200)]
    [ProducesResponseType(404)]
    public async Task<UserDto> Get(
        [FromRoute] int id,
        [FromServices] IUserRepository repository,
        CancellationToken cancellationToken)
    {
        return await repository.GetAsync(id, cancellationToken);
    }

    [HttpPost]
    [DynamicAuthorize(Roles = "Admin")]
    public Task<UserDto> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserDto(1, request.Name));
    }

    [HttpGet("/public")]
    [DynamicAllowAnonymous]
    public string Public() => "hello";
}
```

## OpenAPI

DynamicHttp exposes an OpenAPI provider abstraction. The core package does not force a specific OpenAPI implementation.

```csharp
builder.Services.AddDynamicHttp(options =>
{
    options.OpenApi.Enabled = true;
    options.OpenApi.Provider = new AspNetCoreOpenApiProvider();
});
```

A custom provider can consume the immutable startup endpoint model.

## Validation

Register an implementation of `IDynamicHttpValidator` or an adapter for the validation library used by your application.

## Exceptions

Use `IHttpException` for exceptions that should become a controlled ProblemDetails response. Unknown exceptions are logged and returned as HTTP 500 without exposing implementation details.

## Notes

This package intentionally uses native ASP.NET Core endpoint metadata so authorization, rate limiting, tags, response metadata and other framework features can compose with the generated endpoints.

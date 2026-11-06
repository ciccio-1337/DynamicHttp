using DynamicHttp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDynamicHttp(options =>
{
    options.ScanAssemblies(typeof(Sample.UserService).Assembly);
});
// Dev-only identity provider so the sample's [DynamicAuthorize] endpoints can be exercised
// without a real IdP. It must be explicitly enabled (DevAuth:Enabled=true) and is restricted to
// loopback requests: `dotnet run --project Sample -- --DevAuth:Enabled=true`.
// Do not reuse in production.
bool devAuthEnabled = builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("DevAuth:Enabled");

// Register the development scheme as the default even when disabled: it then returns NoResult,
// so protected endpoints challenge with 401 instead of throwing because no default scheme exists.
// A real deployment must replace it with its own authentication scheme.
builder.Services
    .AddAuthentication(Sample.DevAuthHandler.SchemeName)
    .AddScheme<Sample.DevAuthOptions, Sample.DevAuthHandler>(
        Sample.DevAuthHandler.SchemeName,
        options => options.Enabled = devAuthEnabled);
builder.Services.AddAuthorizationBuilder().AddPolicy("users.read", policy => policy.RequireClaim("users.read", ["true"]));

var app = builder.Build();

if (devAuthEnabled)
{
    app.Logger.LogWarning(
        "Development authentication is enabled: any loopback request with the X-Dev-User header is authenticated. " +
        "Never enable DevAuth outside local development.");
}

app.UseDynamicHttpExceptionHandling();
app.UseAuthentication();
app.UseAuthorization();
app.MapDynamicHttp();
app.Run();

namespace Sample
{
    [HttpService("/api/users", Tags = ["Users"], GroupName = "v1")]
    [DynamicAuthorize(Policy = "users.read")]
    public sealed class UserService
    {
        [HttpGet("/{id}")]
        [ProducesResponseType(200, typeof(UserDto))]
        [ProducesResponseType(404)]
        public Task<UserDto> Get([FromRoute] int id)
        {
            if (id <= 0)
            {
                throw new NotFoundHttpException($"User {id} was not found.");
            }

            return Task.FromResult(new UserDto(id, "Mario Rossi"));
        }

        [HttpPost]
        [DynamicAuthorize(Roles = "Admin")]
        [ProducesResponseType(201, typeof(UserDto))]
        public IResult Create([FromBody] CreateUserRequest request) =>
            Results.Created("/api/users/1", new UserDto(1, request.Name));

        [HttpGet("/public")]
        [DynamicAllowAnonymous]
        public string Public() => "hello";
    }

    public sealed record UserDto(int Id, string Name);
    public sealed record CreateUserRequest(string Name);
}

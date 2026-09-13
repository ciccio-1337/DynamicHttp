using DynamicHttp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDynamicHttp(options =>
{
    options.ScanAssemblies(typeof(Sample.UserService).Assembly);
});
// Dev-only identity provider so the sample's [DynamicAuthorize] endpoints can be exercised
// without a real IdP, e.g. `curl -H 'X-Dev-User: mario' ...`. Do not reuse in production.
builder.Services
    .AddAuthentication(Sample.DevAuthHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Sample.DevAuthHandler>(
        Sample.DevAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder().AddPolicy("users.read", policy => policy.RequireClaim("users.read", ["true"]));

var app = builder.Build();

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

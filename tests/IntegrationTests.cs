using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class IntegrationTests
{
    private static async Task<WebApplication> StartAppAsync(Action<IServiceCollection>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));
        configure?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.MapDynamicHttp();

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Task_endpoint_returns_result()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/task/3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"task-3\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValueTask_endpoint_returns_result()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/valuetask/4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"valuetask-4\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plain_value_endpoint_returns_result()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"plain\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Void_endpoint_is_mapped_and_returns_ok()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/void");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NonGeneric_task_endpoint_returns_ok()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/taskonly");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IResult_endpoint_is_returned_as_is()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/iresult");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("from-iresult", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Body_endpoint_binds_json_and_returns_created()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var content = new StringContent("""{"name":"mario","age":42}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/values/body", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/values/body", response.Headers.Location?.ToString());
        Assert.Contains("\"name\":\"mario\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Body_endpoint_with_empty_body_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/api/values/body", new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Body_endpoint_with_malformed_json_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/api/values/body", new StringContent("{", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_endpoint_with_wrong_content_type_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/api/values/body", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_endpoint_binds_parameters()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var pageOnly = await client.GetAsync("/api/values/query?page=2");
        Assert.Equal("\"2|\"", await pageOnly.Content.ReadAsStringAsync());

        var both = await client.GetAsync("/api/values/query?page=2&count=5");
        Assert.Equal("\"2|5\"", await both.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query_endpoint_missing_required_parameter_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/query");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_endpoint_with_invalid_value_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/query?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_endpoint_uses_declared_default_value()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var defaulted = await client.GetAsync("/api/values/defaults");
        Assert.Equal("\"10\"", await defaulted.Content.ReadAsStringAsync());

        var explicitValue = await client.GetAsync("/api/values/defaults?page=7");
        Assert.Equal("\"7\"", await explicitValue.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query_endpoint_uses_declared_defaults_for_reference_and_nullable()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var allMissing = await client.GetAsync("/api/values/mixeddefaults");
        Assert.Equal("\"fast|5\"", await allMissing.Content.ReadAsStringAsync());

        var oneMissing = await client.GetAsync("/api/values/mixeddefaults?mode=slow");
        Assert.Equal("\"slow|5\"", await oneMissing.Content.ReadAsStringAsync());

        var allPresent = await client.GetAsync("/api/values/mixeddefaults?mode=slow&count=9");
        Assert.Equal("\"slow|9\"", await allPresent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Header_endpoint_reads_named_header()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/values/header");
        request.Headers.TryAddWithoutValidation("X-Trace", "abc");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"abc\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Guid_route_binds_and_invalid_value_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var id = Guid.NewGuid();
        var ok = await client.GetAsync($"/api/values/guid/{id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal($"\"{id}\"", await ok.Content.ReadAsStringAsync());

        var invalid = await client.GetAsync("/api/values/guid/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Enum_route_binds_and_invalid_value_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var ok = await client.GetAsync("/api/values/enum/blue");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"Blue\"", await ok.Content.ReadAsStringAsync());

        var invalid = await client.GetAsync("/api/values/enum/magenta");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task TimeSpan_route_binds_via_type_converter()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var ok = await client.GetAsync("/api/values/time/01:02:03");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"01:02:03\"", await ok.Content.ReadAsStringAsync());

        var invalid = await client.GetAsync("/api/values/time/not-a-time");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task DateOnly_route_binds_via_type_converter()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/date/2024-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2024-01-31\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FromServices_parameter_is_resolved_from_di()
    {
        await using WebApplication app = await StartAppAsync(services => services.AddScoped<Greeter>());
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/greet/bob");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"hi bob\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CancellationToken_parameter_receives_request_aborted()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/cancellation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Catch_all_route_binds_entire_remaining_path()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/catch/a/b/c");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"a/b/c\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddDynamicHttp_without_options_scans_calling_assembly()
    {
        // No configure callback: the fallback must scan the assembly that called AddDynamicHttp
        // (this test assembly) and register ValuesService from it.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp();
        WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.MapDynamicHttp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"plain\"", await response.Content.ReadAsStringAsync());
    }
}

[HttpService("/api/values")]
public sealed class ValuesService
{
    [HttpGet("/task/{id}")]
    public Task<string> TaskResult([FromRoute] int id) => Task.FromResult($"task-{id}");

    [HttpGet("/valuetask/{id}")]
    public ValueTask<string> ValueTaskResult([FromRoute] int id) => ValueTask.FromResult($"valuetask-{id}");

    [HttpGet("/plain")]
    public string Plain() => "plain";

    [HttpGet("/void")]
    public void Void() { }

    [HttpGet("/taskonly")]
    public Task TaskOnly() => Task.CompletedTask;

    [HttpGet("/iresult")]
    public IResult IResult() => Results.Text("from-iresult");

    [HttpPost("/body")]
    public IResult Create([FromBody] BodyRequest body) => Results.Created("/api/values/body", body);

    [HttpGet("/query")]
    public string Query([FromQuery] int page, [FromQuery("count")] int? count) => $"{page}|{count}";

    [HttpGet("/defaults")]
    public string Defaults([FromQuery] int page = 10) => page.ToString();

    [HttpGet("/mixeddefaults")]
    public string MixedDefaults([FromQuery] string mode = "fast", [FromQuery] int? count = 5) => $"{mode}|{count}";

    [HttpGet("/header")]
    public string Header([FromHeader("X-Trace")] string trace) => trace;

    [HttpGet("/guid/{id}")]
    public string GuidRoute([FromRoute] Guid id) => id.ToString();

    [HttpGet("/enum/{color}")]
    public string EnumRoute([FromRoute] Color color) => color.ToString();

    [HttpGet("/time/{t}")]
    public string TimeRoute([FromRoute] TimeSpan t) => t.ToString();

    [HttpGet("/date/{d}")]
    public string DateRoute([FromRoute] DateOnly d) => d.ToString("yyyy-MM-dd");

    [HttpGet("/greet/{name}")]
    public string Greet([FromRoute] string name, [FromServices] Greeter greeter) => greeter.Greet(name);

    [HttpGet("/cancellation")]
    public async Task<string> Cancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
        return "ok";
    }

    [HttpGet("/catch/{*path}")]
    public string CatchAll([FromRoute] string path) => path;
}

public enum Color { Red, Green, Blue }

public sealed record BodyRequest(string Name, int Age);

public sealed class Greeter
{
    public string Greet(string name) => $"hi {name}";
}
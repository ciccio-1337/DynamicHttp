using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Claims;
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
    private static async Task<WebApplication> StartAppAsync(
        Action<IServiceCollection>? configure = null,
        Action<DynamicHttpOptions>? configureOptions = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            configureOptions?.Invoke(options);
        });
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
    public async Task Value_type_return_is_serialized()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/int");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("42", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Value_type_task_return_is_serialized()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/taskint/7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("7", await response.Content.ReadAsStringAsync());
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
        await using WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.MapDynamicHttp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"plain\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nullable_body_accepts_json_null()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        foreach (string path in new[] { "/api/values/nullablebody", "/api/values/nullableint" })
        {
            var response = await client.PostAsync(path,
                new StringContent("null", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"<null>\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Unsupported_body_type_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/values/form",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Route_parameter_uses_inline_default()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var defaulted = await client.GetAsync("/api/values/route-default");
        Assert.Equal("\"10\"", await defaulted.Content.ReadAsStringAsync());

        var explicitValue = await client.GetAsync("/api/values/route-default/7");
        Assert.Equal("\"7\"", await explicitValue.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Framework_types_are_injected()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal("\"/api/values/httpcontext\"",
            await client.GetStringAsync("/api/values/httpcontext"));
        Assert.Equal("\"GET\"", await client.GetStringAsync("/api/values/httprequest"));
        Assert.Equal("\"200\"", await client.GetStringAsync("/api/values/httpresponse"));
        Assert.Equal("\"anon\"", await client.GetStringAsync("/api/values/user"));
    }

    [Fact]
    public async Task Async_result_shapes_are_supported()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal("task-iresult", await client.GetStringAsync("/api/values/taskiresult"));
        Assert.Equal("valuetask-iresult", await client.GetStringAsync("/api/values/valuetaskiresult"));
        Assert.Equal("42", await client.GetStringAsync("/api/values/valuetaskint"));

        var valueTaskOnly = await client.GetAsync("/api/values/valuetaskonly");
        Assert.Equal(HttpStatusCode.OK, valueTaskOnly.StatusCode);
    }

    [Fact]
    public async Task Faulted_task_returns_internal_server_error()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/taskfault");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Faulted_value_task_returns_internal_server_error()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/valuetaskfault");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Custom_task_subclass_result_is_not_lost()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/customtask");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"custom-task\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enum_numeric_overflow_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/enum/99999999999999999999");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Undefined_enum_numeric_value_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/enum/999");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_value_is_not_echoed_in_problem_details()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/query?page=supersecret-value");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("supersecret-value", body, StringComparison.Ordinal);
        Assert.Contains("detail", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Body_over_configured_limit_returns_payload_too_large()
    {
        await using WebApplication app = await StartAppAsync(
            configureOptions: options => options.MaxRequestBodyBytes = 8);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/values/body",
            new StringContent("""{"name":"mario","age":42}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Parameter_validation_attribute_is_enforced()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/values/validated?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/values/validated?page=5")).StatusCode);
    }

    [Fact]
    public async Task Body_model_validation_attribute_is_enforced()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var invalid = await client.PostAsync("/api/values/validatedbody",
            new StringContent("""{"name":""}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var valid = await client.PostAsync("/api/values/validatedbody",
            new StringContent("""{"name":"ok"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Missing_value_on_nullable_oblivious_service_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/nullability-disabled/missing");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Antiforgery_attribute_requires_a_token()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));

        await using WebApplication app = builder.Build();
        app.UseRouting();
        app.UseDynamicHttpExceptionHandling();
        app.UseAntiforgery();
        app.MapDynamicHttp();
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsync("/api/values/antiforgery",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Decimal_overflow_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/decimal/99999999999999999999999999999999");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_required_header_returns_bad_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/values/header");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scoped_services_are_created_per_request()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        string first = await client.GetStringAsync("/api/scoped");
        string second = await client.GetStringAsync("/api/scoped");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Unexpected_exception_does_not_leak_details()
    {
        await using WebApplication app = await StartAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/explode");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("boom", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.Contains("traceId", body, StringComparison.Ordinal);
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

    [HttpPost("/nullablebody")]
    public string NullableBody([FromBody] string? value) => value ?? "<null>";

    [HttpPost("/nullableint")]
    public string NullableInt([FromBody] int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "<null>";

    [HttpPost("/form")]
    public string Form([FromBody] IFormFile file) => file.FileName;

    [HttpGet("/route-default/{page=10}")]
    public string RouteDefault([FromRoute] int page) => page.ToString(CultureInfo.InvariantCulture);

    [HttpGet("/httpcontext")]
    public string HttpContextParam(HttpContext context) => context.Request.Path.ToString();

    [HttpGet("/httprequest")]
    public string HttpRequestParam(HttpRequest request) => request.Method;

    [HttpGet("/httpresponse")]
    public string HttpResponseParam(HttpResponse response) =>
        response.StatusCode.ToString(CultureInfo.InvariantCulture);

    [HttpGet("/user")]
    public string UserParam(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? "auth" : "anon";

    [HttpGet("/taskiresult")]
    public Task<IResult> TaskIResult() => Task.FromResult(Results.Text("task-iresult"));

    [HttpGet("/valuetaskiresult")]
    public ValueTask<IResult> ValueTaskIResult() =>
        ValueTask.FromResult(Results.Text("valuetask-iresult"));

    [HttpGet("/valuetaskint")]
    public ValueTask<int> ValueTaskInt() => ValueTask.FromResult(42);

    [HttpGet("/valuetaskonly")]
    public ValueTask ValueTaskOnly() => ValueTask.CompletedTask;

    [HttpGet("/taskfault")]
    public Task<string> TaskFault() =>
        Task.FromException<string>(new InvalidOperationException("task-boom"));

    [HttpGet("/valuetaskfault")]
    public ValueTask<string> ValueTaskFault() =>
        ValueTask.FromException<string>(new InvalidOperationException("valuetask-boom"));

    [HttpGet("/abort")]
    public string Abort(HttpContext context)
    {
        context.Abort();
        throw new OperationCanceledException(context.RequestAborted);
    }

    [HttpGet("/customtask")]
    public Task<string> CustomTask() => CustomTask<string>.StartNew("custom-task");

    [HttpGet("/decimal/{value}")]
    public string DecimalRoute([FromRoute] decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    [HttpGet("/header")]
    public string Header([FromHeader("X-Trace")] string trace) => trace;

    [HttpGet("/guid/{id}")]
    public string GuidRoute([FromRoute] Guid id) => id.ToString();

    [HttpGet("/enum/{color}")]
    public string EnumRoute([FromRoute] Color color) => color.ToString();

    [HttpGet("/validated")]
    public string Validated([FromQuery, Range(1, 10)] int page) =>
        page.ToString(CultureInfo.InvariantCulture);

    [HttpPost("/validatedbody")]
    public string ValidatedBody([FromBody] ValidatedBodyRequest request) => request.Name;

    [HttpPost("/antiforgery")]
    [DynamicAntiforgery]
    public string Antiforgery() => "antiforgery";

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

    [HttpGet("/int")]
    public int Int() => 42;

    [HttpGet("/taskint/{id}")]
    public Task<int> TaskInt([FromRoute] int id) => Task.FromResult(id);
}

public enum Color { Red, Green, Blue }

[HttpService("/api/scoped")]
public sealed class ScopedProbeService
{
    public Guid Id { get; } = Guid.NewGuid();

    [HttpGet]
    public string Get() => Id.ToString();
}

public sealed record BodyRequest(string Name, int Age);

public sealed record ValidatedBodyRequest([property: Required] string Name);

public sealed class CustomTask<T>(T result) : Task<T>(() => result)
{
    // Task<T>(Func<T>) creates a cold task; callers must start it before awaiting it.
    public static CustomTask<T> StartNew(T result)
    {
        var task = new CustomTask<T>(result);
        task.Start(TaskScheduler.Default);

        return task;
    }
}

public sealed class Greeter
{
    public string Greet(string name) => $"hi {name}";
}

using DynamicHttp.Tests.Fixtures;
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
    public void Catch_all_route_parameter_binds_to_route_name_without_star()
    {
        var endpoints = DynamicHttpDiscovery.Build([typeof(CatchAllService).Assembly]);
        var endpoint = Assert.Single(endpoints, x => x.ServiceType == typeof(CatchAllService));

        Assert.Equal("/api/catch/{*path}", endpoint.Route);
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

    [Fact]
    public void By_ref_parameter_throws_configuration_error()
    {
        // The bad service lives in a dedicated fixture assembly so scanning it cannot
        // affect any other test that builds over the main test assembly.
        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.Build([typeof(RefParameterService).Assembly]));

        Assert.Contains("ref", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Method_with_multiple_http_attributes_throws()
    {
        var method = typeof(MultipleHttpMethods).GetMethod(nameof(MultipleHttpMethods.Get))!;

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.GetHttpAttribute(typeof(MultipleHttpMethods), method));

        Assert.Contains("only one HTTP method attribute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Static_endpoint_method_throws()
    {
        var method = typeof(StaticEndpoint).GetMethod(nameof(StaticEndpoint.Get))!;

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateEndpointMethod(typeof(StaticEndpoint), method));

        Assert.Contains("static", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_endpoint_method_throws()
    {
        var method = typeof(GenericEndpoint).GetMethod(nameof(GenericEndpoint.Get))!;

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateEndpointMethod(typeof(GenericEndpoint), method));

        Assert.Contains("Generic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parameter_with_multiple_binding_attributes_throws()
    {
        var method = typeof(MultipleBindingAttributes).GetMethod(nameof(MultipleBindingAttributes.Get))!;

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.BuildParameters(typeof(MultipleBindingAttributes), method));

        Assert.Contains("multiple binding attributes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_parameter_not_in_template_throws()
    {
        var method = typeof(BadRouteParameter).GetMethod(nameof(BadRouteParameter.Get))!;
        var parameters = DynamicHttpDiscovery.BuildParameters(typeof(BadRouteParameter), method);

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateParameters("/api/bad", parameters, typeof(BadRouteParameter), method));

        Assert.Contains("no parameter named 'id'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_body_parameters_throw()
    {
        var method = typeof(MultipleBodies).GetMethod(nameof(MultipleBodies.Get))!;
        var parameters = DynamicHttpDiscovery.BuildParameters(typeof(MultipleBodies), method);

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateParameters("/api/bodies", parameters, typeof(MultipleBodies), method));

        Assert.Contains("at most one body parameter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_generic_service_throws()
    {
        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateServiceType(typeof(GenericService<>)));

        Assert.Contains("generic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ambiguous_route_templates_conflict()
    {
        EndpointDefinition[] endpoints =
        [
            CreateEndpoint("/api/{id}"),
            CreateEndpoint("/api/{name}")
        ];

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            DynamicHttpDiscovery.ValidateConflicts(endpoints));

        Assert.Contains("/api/{id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/api/{name}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constrained_routes_are_not_treated_as_conflicts()
    {
        EndpointDefinition[] endpoints =
        [
            CreateEndpoint("/api/{id}"),
            CreateEndpoint("/api/{id:int}")
        ];

        DynamicHttpDiscovery.ValidateConflicts(endpoints);
    }

    [Fact]
    public void Optional_and_constrained_route_parameters_are_accepted()
    {
        var endpoints = DynamicHttpDiscovery.Build([typeof(RouteTemplateService).Assembly]);

        Assert.Contains(endpoints, x => x.Route == "/api/templates/optional/{id?}");
        Assert.Contains(endpoints, x => x.Route == "/api/templates/constrained/{id:int}");
    }

    private static EndpointDefinition CreateEndpoint(string route) => new(
        typeof(DiscoveryTests),
        typeof(object).GetMethod(nameof(ToString))!,
        route,
        "GET",
        [],
        static (_, _) => null,
        false,
        [],
        [],
        [],
        null);


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

    [HttpService("/api/catch")]
    private sealed class CatchAllService
    {
        [HttpGet("/{*path}")]
        public string Get([FromRoute] string path) => path;
    }

    [HttpService("/api/templates")]
    private sealed class RouteTemplateService
    {
        [HttpGet("/optional/{id?}")]
        public string Optional([FromRoute] int? id) => id?.ToString() ?? "none";

        [HttpGet("/constrained/{id:int}")]
        public string Constrained([FromRoute] int id) => id.ToString();
    }

    private sealed class MultipleHttpMethods
    {
        [HttpGet]
        [HttpPost]
        public string Get() => "x";
    }

    private sealed class StaticEndpoint
    {
        [HttpGet]
        public static string Get() => "x";
    }

    private sealed class GenericEndpoint
    {
        [HttpGet]
        public T Get<T>() => default!;
    }

    private sealed class MultipleBindingAttributes
    {
        public string Get([FromQuery][FromBody] string value) => value;
    }

    private sealed class BadRouteParameter
    {
        public string Get([FromRoute] int id) => id.ToString();
    }

    private sealed class MultipleBodies
    {
        public string Get(BodyOne first, BodyTwo second) => $"{first.GetHashCode()}{second.GetHashCode()}";
    }

    private sealed class BodyOne { }

    private sealed class BodyTwo { }

    private sealed class GenericService<T>
    {
        public T Value { get; } = default!;
    }
}

namespace DynamicHttp;

/// <summary>Marks a class as a DynamicHttp service.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class HttpServiceAttribute(string routePrefix = "") : Attribute
{
    public string RoutePrefix { get; } = routePrefix;
    public string[] Tags { get; init; } = [];
    public string? GroupName { get; init; }
}

[AttributeUsage(AttributeTargets.Method)]
public abstract class HttpMethodAttribute(string template = "") : Attribute
{
    public string Template { get; } = template;
    public string[] Tags { get; init; } = [];

    /// <summary>
    /// The HTTP method (verb) handled by the endpoint, for example <c>GET</c>. Subclasses may
    /// provide custom verbs such as <c>PROPFIND</c>.
    /// </summary>
    public abstract string Method { get; }
}

public sealed class HttpGetAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "GET";
}

public sealed class HttpPostAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "POST";
}

public sealed class HttpPutAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "PUT";
}

public sealed class HttpPatchAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "PATCH";
}

public sealed class HttpDeleteAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "DELETE";
}

public sealed class HttpHeadAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "HEAD";
}

public sealed class HttpOptionsAttribute(string template = "") : HttpMethodAttribute(template)
{
    public override string Method => "OPTIONS";
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromRouteAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromQueryAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class DynamicAuthorizeAttribute : Attribute
{
    public string? Policy { get; init; }
    public string? Roles { get; init; }
    public string? AuthenticationSchemes { get; init; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DynamicAllowAnonymousAttribute : Attribute;

/// <summary>
/// Requires ASP.NET Core antiforgery validation for the endpoint (or every endpoint of the
/// service). Register <c>AddAntiforgery()</c>; use this on cookie-authenticated state-changing
/// endpoints to protect against CSRF.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DynamicAntiforgeryAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class ProducesResponseTypeAttribute(int statusCode, Type? responseType = null) : Attribute
{
    public int StatusCode { get; } = statusCode;
    public Type? ResponseType { get; } = responseType;
}

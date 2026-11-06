using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicHttp;

public sealed class DynamicHttpOptions
{
    private ServiceLifetime _defaultServiceLifetime = ServiceLifetime.Scoped;
    private bool _requireAuthorizationByDefault;
    private bool _requireAntiforgeryByDefault;
    private bool _validateModels = true;
    private long? _maxRequestBodyBytes;

    internal List<Assembly> Assemblies { get; } = [];

    public ServiceLifetime DefaultServiceLifetime
    {
        get => _defaultServiceLifetime;
        set
        {
            _defaultServiceLifetime = value;
            DefaultServiceLifetimeSet = true;
        }
    }

    /// <summary>
    /// When true, endpoints without <c>[DynamicAuthorize]</c>, a framework <c>[Authorize]</c>
    /// or an allow-anonymous marker (<c>[DynamicAllowAnonymous]</c>/<c>[AllowAnonymous]</c>)
    /// require an authenticated user. Defaults to false to preserve the opt-in behavior of
    /// previous versions; enable it in new services.
    /// </summary>
    public bool RequireAuthorizationByDefault
    {
        get => _requireAuthorizationByDefault;
        set
        {
            _requireAuthorizationByDefault = value;
            RequireAuthorizationByDefaultSet = true;
        }
    }

    /// <summary>
    /// When true, every endpoint whose HTTP method is not GET, HEAD, OPTIONS or TRACE requires
    /// a valid antiforgery token, even without <c>[DynamicAntiforgery]</c>. Defaults to false.
    /// Register <c>AddAntiforgery()</c>; the check fails closed when antiforgery services are
    /// missing. An explicit <c>[DynamicAntiforgery]</c> is always honored, including on safe
    /// methods such as GET.
    /// </summary>
    public bool RequireAntiforgeryByDefault
    {
        get => _requireAntiforgeryByDefault;
        set
        {
            _requireAntiforgeryByDefault = value;
            RequireAntiforgeryByDefaultSet = true;
        }
    }

    /// <summary>
    /// When true (default), DataAnnotations validation attributes on parameters and on body
    /// models are enforced after binding. When false, validation is skipped entirely. Errors
    /// produce a 400 ProblemDetails response.
    /// </summary>
    public bool ValidateModels
    {
        get => _validateModels;
        set
        {
            _validateModels = value;
            ValidateModelsSet = true;
        }
    }

    /// <summary>
    /// Optional maximum request body size, in bytes, applied to every dynamic endpoint before
    /// binding. Requests that exceed the limit are rejected with 413. When null, the server's
    /// default limit applies.
    /// </summary>
    public long? MaxRequestBodyBytes
    {
        get => _maxRequestBodyBytes;
        set
        {
            _maxRequestBodyBytes = value;
            MaxRequestBodyBytesSet = true;
        }
    }

    internal bool DefaultServiceLifetimeSet { get; private set; }
    internal bool RequireAuthorizationByDefaultSet { get; private set; }
    internal bool RequireAntiforgeryByDefaultSet { get; private set; }
    internal bool ValidateModelsSet { get; private set; }
    internal bool MaxRequestBodyBytesSet { get; private set; }

    public void ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            Assemblies.Add(assembly);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ScanCallingAssembly() => ScanAssemblies(Assembly.GetCallingAssembly());
}

public static class DynamicHttpServiceCollectionExtensions
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddDynamicHttp(this IServiceCollection services, Action<DynamicHttpOptions>? configure = null)
    {
        DynamicHttpOptions options = new();

        configure?.Invoke(options);

        if (options.MaxRequestBodyBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                "MaxRequestBodyBytes must be a positive number of bytes or null.");
        }

        if (options.Assemblies.Count == 0)
        {
            // GetCallingAssembly() here resolves to the assembly that invoked AddDynamicHttp
            // (the consumer's app), not DynamicHttp itself. NoInlining keeps that frame intact.
            options.ScanAssemblies(Assembly.GetCallingAssembly());
        }

        ServiceDescriptor[] descriptors = [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(DynamicHttpOptions))];

        DynamicHttpOptions? existing = descriptors
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<DynamicHttpOptions>()
            .FirstOrDefault();

        if (descriptors.Any(descriptor => descriptor.ImplementationInstance is not DynamicHttpOptions))
        {
            throw new DynamicHttpConfigurationException(
                "DynamicHttp configuration error: DynamicHttpOptions is already registered without an " +
                "instance. Configure options through AddDynamicHttp(options => ...) instead of " +
                "registering DynamicHttpOptions separately.");
        }

        if (existing is null)
        {
            services.AddSingleton(options);
            existing = options;
        }
        else
        {
            // Keep the first registration and merge the new settings into it. Repeated calls are
            // additive for assemblies, but scalar options must not silently disagree.
            ApplyOptions(existing, options);
            existing.ScanAssemblies([.. options.Assemblies]);
        }

        services.TryAddSingleton<DynamicHttpRegistry>();
        RegisterDiscoveredServices(services, existing);

        return services;
    }

    private static void ApplyOptions(DynamicHttpOptions existing, DynamicHttpOptions options)
    {
        if (options.DefaultServiceLifetimeSet &&
            options.DefaultServiceLifetime != existing.DefaultServiceLifetime)
        {
            // Services discovered by the first call were already registered with the old lifetime,
            // so changing it now would leave the container in an inconsistent state.
            throw OptionConflict(nameof(DynamicHttpOptions.DefaultServiceLifetime));
        }

        if (options.DefaultServiceLifetimeSet)
        {
            existing.DefaultServiceLifetime = options.DefaultServiceLifetime;
        }

        if (options.RequireAuthorizationByDefaultSet)
        {
            if (existing.RequireAuthorizationByDefaultSet &&
                existing.RequireAuthorizationByDefault != options.RequireAuthorizationByDefault)
            {
                throw OptionConflict(nameof(DynamicHttpOptions.RequireAuthorizationByDefault));
            }

            existing.RequireAuthorizationByDefault = options.RequireAuthorizationByDefault;
        }

        if (options.RequireAntiforgeryByDefaultSet)
        {
            if (existing.RequireAntiforgeryByDefaultSet &&
                existing.RequireAntiforgeryByDefault != options.RequireAntiforgeryByDefault)
            {
                throw OptionConflict(nameof(DynamicHttpOptions.RequireAntiforgeryByDefault));
            }

            existing.RequireAntiforgeryByDefault = options.RequireAntiforgeryByDefault;
        }

        if (options.ValidateModelsSet)
        {
            if (existing.ValidateModelsSet && existing.ValidateModels != options.ValidateModels)
            {
                throw OptionConflict(nameof(DynamicHttpOptions.ValidateModels));
            }

            existing.ValidateModels = options.ValidateModels;
        }

        if (options.MaxRequestBodyBytesSet)
        {
            if (existing.MaxRequestBodyBytesSet &&
                existing.MaxRequestBodyBytes != options.MaxRequestBodyBytes)
            {
                throw OptionConflict(nameof(DynamicHttpOptions.MaxRequestBodyBytes));
            }

            existing.MaxRequestBodyBytes = options.MaxRequestBodyBytes;
        }
    }

    private static DynamicHttpConfigurationException OptionConflict(string propertyName) =>
        new($"DynamicHttp configuration error: '{propertyName}' was configured differently by a " +
            "later AddDynamicHttp call. Configure each option once, in the first call.");

    private static void RegisterDiscoveredServices(IServiceCollection services, DynamicHttpOptions options)
    {
        foreach (var assembly in options.Assemblies.Distinct())
        {
            foreach (var type in DynamicHttpDiscovery.FindServices(assembly))
            {
                // TryAdd preserves registrations made by the application (custom lifetime,
                // factory or implementation) and makes repeated scans a no-op.
                services.TryAdd(new ServiceDescriptor(type, type, options.DefaultServiceLifetime));
            }
        }
    }
}

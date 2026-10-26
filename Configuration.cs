using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicHttp;

public sealed class DynamicHttpOptions
{
    internal List<Assembly> Assemblies { get; } = [];

    public ServiceLifetime DefaultServiceLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// When true, endpoints without <c>[DynamicAuthorize]</c>, a framework <c>[Authorize]</c>
    /// or an allow-anonymous marker (<c>[DynamicAllowAnonymous]</c>/<c>[AllowAnonymous]</c>)
    /// require an authenticated user. Defaults to false to preserve the opt-in behavior of
    /// previous versions; enable it in new services.
    /// </summary>
    public bool RequireAuthorizationByDefault { get; set; }

    /// <summary>
    /// When true (default), DataAnnotations validation attributes on parameters and on body
    /// models are enforced after binding. Errors produce a 400 ProblemDetails response.
    /// </summary>
    public bool ValidateModels { get; set; } = true;

    /// <summary>
    /// Optional maximum request body size, in bytes, applied to every dynamic endpoint before
    /// binding. Requests that exceed the limit are rejected with 413. When null, the server's
    /// default limit applies.
    /// </summary>
    public long? MaxRequestBodyBytes { get; set; }

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

        DynamicHttpOptions? existing = services
            .Where(descriptor => descriptor.ServiceType == typeof(DynamicHttpOptions))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<DynamicHttpOptions>()
            .FirstOrDefault();

        if (existing is null)
        {
            services.AddSingleton(options);
            existing = options;
        }
        else
        {
            // Keep the first registration (and its already-built registry) and merge the new
            // assemblies into it so repeated AddDynamicHttp calls are additive and idempotent.
            existing.ScanAssemblies([.. options.Assemblies]);
        }

        services.TryAddSingleton<DynamicHttpRegistry>();
        RegisterDiscoveredServices(services, existing);

        return services;
    }

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

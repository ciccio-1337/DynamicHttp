using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicHttp;

public sealed class DynamicHttpOptions
{
    internal List<Assembly> Assemblies { get; } = [];

    public ServiceLifetime DefaultServiceLifetime { get; set; } = ServiceLifetime.Scoped;

    public void ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            Assemblies.Add(assembly);
        }
    }

    public void ScanCallingAssembly() => ScanAssemblies(Assembly.GetCallingAssembly());
}

public static class DynamicHttpServiceCollectionExtensions
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddDynamicHttp(this IServiceCollection services, Action<DynamicHttpOptions>? configure = null)
    {
        DynamicHttpOptions options = new();

        configure?.Invoke(options);

        if (options.Assemblies.Count == 0)
        {
            // GetCallingAssembly() here resolves to the assembly that invoked AddDynamicHttp
            // (the consumer's app), not DynamicHttp itself. NoInlining keeps that frame intact.
            options.ScanAssemblies(Assembly.GetCallingAssembly());
        }

        services.AddSingleton(options);
        services.AddSingleton<DynamicHttpRegistry>();

        foreach (var assembly in options.Assemblies.Distinct())
        {
            foreach (var type in DynamicHttpDiscovery.FindServices(assembly))
            {
                services.Add(new ServiceDescriptor(type, type, options.DefaultServiceLifetime));
            }
        }

        return services;
    }
}

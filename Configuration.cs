using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicHttp;

public sealed class DynamicHttpOptions
{
    internal List<Assembly> Assemblies { get; } = [];

    public bool ValidateOnStartup { get; set; } = true;
    public ServiceLifetime DefaultServiceLifetime { get; set; } = ServiceLifetime.Scoped;
    public DynamicHttpOpenApiOptions OpenApi { get; } = new();

    public void ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            Assemblies.Add(assembly);
        }
    }

    public void ScanCallingAssembly() => ScanAssemblies(Assembly.GetCallingAssembly());
}

public sealed class DynamicHttpOpenApiOptions
{
    public bool Enabled { get; set; } = true;
    public IDynamicHttpOpenApiProvider? Provider { get; set; }
}

public static class DynamicHttpServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicHttp(this IServiceCollection services, Action<DynamicHttpOptions>? configure = null)
    {
        DynamicHttpOptions options = new();

        configure?.Invoke(options);

        if (options.Assemblies.Count == 0)
        {
            options.ScanCallingAssembly();
        }

        services.AddSingleton(options);
        services.AddSingleton<DynamicHttpRegistry>();
        services.AddSingleton<IDynamicHttpValidator, NoOpValidator>();

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

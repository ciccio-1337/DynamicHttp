using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ScanCallingAssembly_uses_calling_assembly()
    {
        var options = new DynamicHttpOptions();

        options.ScanCallingAssembly();

        Assert.Contains(typeof(ConfigurationTests).Assembly, options.Assemblies);
    }

    [Fact]
    public void AddDynamicHttp_preserves_existing_service_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ValuesService>();

        services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(ValuesService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddDynamicHttp_is_idempotent()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));
        services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));

        Assert.Single(services, x => x.ServiceType == typeof(DynamicHttpOptions));
        Assert.Single(services, x => x.ServiceType == typeof(DynamicHttpRegistry));
        Assert.Single(services, x => x.ServiceType == typeof(ValuesService));
    }

    [Fact]
    public void AddDynamicHttp_uses_scoped_lifetime_by_default()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(ValuesService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddDynamicHttp_uses_configured_default_lifetime()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            options.DefaultServiceLifetime = ServiceLifetime.Transient;
        });

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(ValuesService));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    [Fact]
    public void AddDynamicHttp_rejects_non_positive_body_limit()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddDynamicHttp(options =>
            {
                options.ScanAssemblies(typeof(ValuesService).Assembly);
                options.MaxRequestBodyBytes = 0;
            }));
    }
}

public sealed class AwaitingTests
{
    [Fact]
    public async Task Custom_task_subclass_result_is_read()
    {
        object? result = await DynamicHttpAwaiting.AwaitAsync(CustomTask<string>.StartNew("x"));

        Assert.Equal("x", result);
    }
}

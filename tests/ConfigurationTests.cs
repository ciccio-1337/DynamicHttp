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

    [Fact]
    public void AddDynamicHttp_rejects_factory_registered_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DynamicHttpOptions>(_ => new DynamicHttpOptions());

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            services.AddDynamicHttp(options =>
                options.ScanAssemblies(typeof(ValuesService).Assembly)));

        Assert.Contains("without an instance", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDynamicHttp_rejects_conflicting_options_on_repeated_calls()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            options.MaxRequestBodyBytes = 8;
        });

        var exception = Assert.Throws<DynamicHttpConfigurationException>(() =>
            services.AddDynamicHttp(options =>
            {
                options.ScanAssemblies(typeof(ValuesService).Assembly);
                options.MaxRequestBodyBytes = 9;
            }));

        Assert.Contains(nameof(DynamicHttpOptions.MaxRequestBodyBytes), exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddDynamicHttp_rejects_changing_default_service_lifetime_after_registration()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            options.DefaultServiceLifetime = ServiceLifetime.Transient;
        });

        Assert.Throws<DynamicHttpConfigurationException>(() =>
            services.AddDynamicHttp(options =>
            {
                options.ScanAssemblies(typeof(ValuesService).Assembly);
                options.DefaultServiceLifetime = ServiceLifetime.Scoped;
            }));
    }

    [Fact]
    public void AddDynamicHttp_allows_repeating_the_same_option_value()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            options.MaxRequestBodyBytes = 8;
        });
        services.AddDynamicHttp(options =>
        {
            options.ScanAssemblies(typeof(ValuesService).Assembly);
            options.MaxRequestBodyBytes = 8;
        });

        Assert.Single(services, x => x.ServiceType == typeof(DynamicHttpOptions));
    }

    [Fact]
    public void AddDynamicHttp_merges_an_option_set_only_by_a_later_call()
    {
        var services = new ServiceCollection();

        services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));
        services.AddDynamicHttp(options => options.MaxRequestBodyBytes = 12);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<DynamicHttpOptions>();

        Assert.Equal(12, options.MaxRequestBodyBytes);
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

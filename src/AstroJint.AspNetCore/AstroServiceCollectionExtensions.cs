using AstroJint.AspNetCore;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the services that host a built Astro site.</summary>
public static class AstroServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services that run Astro's on-demand pages in Jint. The Astro build is loaded when the host starts,
    /// so a missing or outdated build fails fast. Pair it with <c>UseAstro()</c>, or with
    /// <c>UseAstroStaticFiles()</c> and <c>MapAstro()</c>.
    /// </summary>
    public static IServiceCollection AddAstro(this IServiceCollection services, Action<AstroOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AstroOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AstroOptions>, AstroOptionsValidator>());

        // Where Astro.session keeps its data. A cache that the application registers wins, in either order.
        services.AddDistributedMemoryCache();
        services.TryAddSingleton<AstroApp>();
        services.AddHostedService<AstroWarmupService>();
        return services;
    }

    /// <summary>
    /// Adds ASP.NET Core output caching for the on-demand pages. A page is cached for as long as its own
    /// <c>Cache-Control</c> header allows (<c>s-maxage</c>, else <c>max-age</c>; never with <c>no-store</c>,
    /// <c>private</c> or a <c>Set-Cookie</c>), for anonymous <c>GET</c> and <c>HEAD</c> requests, keyed by the path,
    /// the query string and <see cref="AstroOutputCacheOptions.VaryByHeaders"/>. The copies live in memory, or in
    /// <see cref="AstroOutputCacheOptions.Directory"/> when it is set. Add <c>app.UseOutputCache()</c> before
    /// <c>UseAstro()</c> to turn it on.
    /// </summary>
    public static IServiceCollection AddAstroOutputCache(this IServiceCollection services, Action<AstroOutputCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AstroOutputCacheOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddOutputCache();

        // The store is chosen when it is first needed, as the options may still be bound from configuration: the
        // folder of the options, or the in-memory store that AddOutputCache registered.
        var memoryStore = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IOutputCacheStore));
        if (memoryStore is not null && memoryStore.ImplementationType != typeof(FileOutputCacheStore))
        {
            services.Remove(memoryStore);
            services.AddSingleton(provider =>
            {
                var cacheOptions = provider.GetRequiredService<IOptions<AstroOutputCacheOptions>>().Value;
                if (string.IsNullOrWhiteSpace(cacheOptions.Directory))
                {
                    return CreateFromDescriptor(memoryStore, provider);
                }

                var directory = Path.GetFullPath(cacheOptions.Directory, provider.GetRequiredService<IHostEnvironment>().ContentRootPath);
                return new FileOutputCacheStore(directory, cacheOptions.SweepInterval, provider.GetRequiredService<ILogger<FileOutputCacheStore>>());
            });
        }

        return services;
    }

    private static IOutputCacheStore CreateFromDescriptor(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationInstance is IOutputCacheStore instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (IOutputCacheStore)factory(provider);
        }

        return (IOutputCacheStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
}

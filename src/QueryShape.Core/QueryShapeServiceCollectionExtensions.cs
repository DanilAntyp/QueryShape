using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QueryShape;

/// <summary>DI registration: <c>services.AddQueryShape(o =&gt; ...)</c>.</summary>
public static class QueryShapeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="QueryShapeOptions"/> as a singleton. Pair with <c>optionsBuilder.UseQueryShape()</c> inside <c>AddDbContext</c>;
    /// the options are resolved from the application services. Can be called more than once; every <paramref name="configure"/> runs, in order.
    /// </summary>
    public static IServiceCollection AddQueryShape(this IServiceCollection services, Action<QueryShapeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<QueryShapeOptions>>().Value;
            options.LoggerFactory ??= sp.GetService<ILoggerFactory>();
            return options;
        });
        return services;
    }
}

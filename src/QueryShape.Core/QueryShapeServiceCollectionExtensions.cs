using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QueryShape;

/// <summary>DI registration: <c>services.AddQueryShape(o =&gt; ...)</c>.</summary>
public static class QueryShapeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="QueryShapeOptions"/> as a singleton. Pair with <c>optionsBuilder.UseQueryShape()</c> inside <c>AddDbContext</c>;
    /// the options are resolved from the application services.
    /// </summary>
    public static IServiceCollection AddQueryShape(this IServiceCollection services, Action<QueryShapeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var options = new QueryShapeOptions();
            configure?.Invoke(options);
            options.LoggerFactory ??= sp.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory;
            return options;
        });
        return services;
    }
}

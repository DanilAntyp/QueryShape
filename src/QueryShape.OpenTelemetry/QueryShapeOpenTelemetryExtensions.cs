using Microsoft.Extensions.DependencyInjection;

namespace QueryShape.OpenTelemetry;

/// <summary>Registration helpers.</summary>
public static class QueryShapeOpenTelemetryExtensions
{
    /// <summary>
    /// Enriches spans and emits metrics for every context configured with <c>UseQueryShape()</c>. Call after <c>AddQueryShape()</c>.
    /// Remember to add <c>"QueryShape"</c> as a source/meter to your OpenTelemetry providers if you want its own activities or metrics exported.
    /// </summary>
    public static IServiceCollection AddQueryShapeOpenTelemetry(this IServiceCollection services, Action<QueryShapeOpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var otelOptions = new QueryShapeOpenTelemetryOptions();
        configure?.Invoke(otelOptions);
        var listener = new QueryShapeOpenTelemetryListener(otelOptions);
        services.AddQueryShape(o =>
        {
            if (!o.Listeners.Any(l => l is QueryShapeOpenTelemetryListener))
            {
                o.Listeners.Add(listener);
            }
        });
        return services;
    }

    /// <summary>Adds the enrichment listener to a <see cref="QueryShapeOptions"/> instance (for apps that do not use DI).</summary>
    public static QueryShapeOptions AddOpenTelemetry(this QueryShapeOptions options, Action<QueryShapeOpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var otelOptions = new QueryShapeOpenTelemetryOptions();
        configure?.Invoke(otelOptions);
        if (!options.Listeners.Any(l => l is QueryShapeOpenTelemetryListener))
        {
            options.Listeners.Add(new QueryShapeOpenTelemetryListener(otelOptions));
        }

        return options;
    }
}

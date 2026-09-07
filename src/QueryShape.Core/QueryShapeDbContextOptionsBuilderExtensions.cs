using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using QueryShape.Capture;

namespace QueryShape;

/// <summary>Registers QueryShape's interceptor on a <see cref="DbContextOptionsBuilder"/>.</summary>
public static class QueryShapeDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Captures every query this context runs. With no <paramref name="options"/>, the options registered by
    /// <c>services.AddQueryShape()</c> are used when the builder has an application service provider (i.e. inside <c>AddDbContext</c>),
    /// otherwise <see cref="QueryShapeOptions.Default"/>.
    /// </summary>
    public static DbContextOptionsBuilder UseQueryShape(this DbContextOptionsBuilder builder, QueryShapeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        options ??= ResolveFromApplicationServices(builder) ?? QueryShapeOptions.Default;
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new QueryShapeOptionsExtension(options));

        var existing = builder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (existing is null || !existing.Contains(QueryShapeInterceptor.Instance))
        {
            builder.AddInterceptors(QueryShapeInterceptor.Instance);
        }

        return builder;
    }

    /// <inheritdoc cref="UseQueryShape(DbContextOptionsBuilder, QueryShapeOptions?)"/>
    public static DbContextOptionsBuilder<TContext> UseQueryShape<TContext>(this DbContextOptionsBuilder<TContext> builder, QueryShapeOptions? options = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseQueryShape((DbContextOptionsBuilder)builder, options);

    private static QueryShapeOptions? ResolveFromApplicationServices(DbContextOptionsBuilder builder)
    {
        var appServices = builder.Options.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        return appServices?.GetService(typeof(QueryShapeOptions)) as QueryShapeOptions;
    }
}

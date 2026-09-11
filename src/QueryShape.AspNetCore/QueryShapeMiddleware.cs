using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace QueryShape.AspNetCore;

/// <summary>Options for the request middleware.</summary>
public sealed class QueryShapeMiddlewareOptions
{
    /// <summary>
    /// Add an <c>X-QueryShape</c> response header summarizing the request's queries and diagnoses (e.g. <c>12 queries; QS001 Error, QS004 Warning</c>).
    /// Handy in development, off by default. The header reflects queries that ran before the response started.
    /// </summary>
    public bool EmitResponseHeader { get; set; }

    /// <summary>Name of the response header when <see cref="EmitResponseHeader"/> is on.</summary>
    public string ResponseHeaderName { get; set; } = "X-QueryShape";

    /// <summary>Requests whose path starts with one of these prefixes get no scope (health checks, static files). Default: none.</summary>
    public IList<string> ExcludedPathPrefixes { get; } = new List<string>();
}

/// <summary>Opens a <see cref="QueryShapeScope"/> per request and closes it when the response completes.</summary>
public sealed class QueryShapeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly QueryShapeOptions _options;
    private readonly QueryShapeMiddlewareOptions _middlewareOptions;

    /// <summary>Creates the middleware.</summary>
    public QueryShapeMiddleware(RequestDelegate next, QueryShapeOptions options, QueryShapeMiddlewareOptions middlewareOptions)
    {
        _next = next;
        _options = options;
        _middlewareOptions = middlewareOptions;
    }

    /// <summary>
    /// Runs the rest of the pipeline inside a scope named <c>METHOD /route/template</c> (the request path until routing has chosen an endpoint;
    /// the template afterwards, so <c>/orders/{id}</c> is one name in reports and telemetry instead of one per id).
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var prefix in _middlewareOptions.ExcludedPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        using var scope = QueryShapeScope.Begin(context.Request.Method + " " + path, _options);
        // The request pipeline is asynchronous by construction, and its thread is a pooled one: a synchronous query here blocks it (QS012).
        scope.Annotate(QueryShapeScope.AsyncHostAnnotation, "true");
        context.Items[typeof(QueryShapeScope)] = scope;

        if (_middlewareOptions.EmitResponseHeader)
        {
            context.Response.OnStarting(static state =>
            {
                var (ctx, sc, name) = ((HttpContext, QueryShapeScope, string))state;
                try
                {
                    var diagnoses = sc.Analyze();
                    var summary = sc.CommandCount.ToString(CultureInfo.InvariantCulture) + (sc.CommandCount == 1 ? " query" : " queries");
                    if (diagnoses.Count > 0)
                    {
                        summary += "; " + string.Join(", ", diagnoses.Select(d => d.RuleId + " " + d.Severity));
                    }

                    ctx.Response.Headers[name] = summary;
                }
                catch
                {
                    // Never fail a response because of a header.
                }

                return Task.CompletedTask;
            }, (context, scope, _middlewareOptions.ResponseHeaderName));
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Routing ran inside the pipeline: name the scope by the route template before it is disposed (reported, exported).
            if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } template })
            {
                // Attribute routing writes templates without a leading slash ("Users/Me"), minimal APIs with one.
                // Scope names group reports and telemetry, so they must not depend on which style an app uses.
                scope.Name = context.Request.Method + " " + (template.StartsWith('/') ? template : "/" + template);
            }
        }
    }
}

/// <summary><c>app.UseQueryShape()</c>.</summary>
public static class QueryShapeApplicationBuilderExtensions
{
    /// <summary>
    /// Opens one QueryShape scope per request. Place it early in the pipeline (before endpoints) so every query in the request lands in the scope.
    /// Requires <c>services.AddQueryShape()</c>; without it, <see cref="QueryShapeOptions.Default"/> is used.
    /// </summary>
    public static IApplicationBuilder UseQueryShape(this IApplicationBuilder app, Action<QueryShapeMiddlewareOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetService<QueryShapeOptions>() ?? QueryShapeOptions.Default;
        var middlewareOptions = new QueryShapeMiddlewareOptions();
        configure?.Invoke(middlewareOptions);
        return app.UseMiddleware<QueryShapeMiddleware>(options, middlewareOptions);
    }

    /// <summary>The scope opened by the middleware for this request, or <c>null</c>.</summary>
    public static QueryShapeScope? GetQueryShapeScope(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(typeof(QueryShapeScope), out var s) ? s as QueryShapeScope : null;
    }
}

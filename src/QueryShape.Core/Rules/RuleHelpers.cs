using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QueryShape.Rules;

/// <summary>Small helpers shared by the built-in rules.</summary>
internal static class RuleHelpers
{
    /// <summary>Commands that read data via a LINQ or raw query (not SaveChanges, migrations...).</summary>
    public static IEnumerable<CapturedCommand> ReadQueries(QueryShapeScope scope)
        => scope.Commands.Where(c => c.Source is QuerySource.Linq or QuerySource.Raw
                                     && !c.Failed
                                     && c.ExecuteMethod is DbCommandMethod.ExecuteReader or DbCommandMethod.ExecuteScalar
                                     && !(c.Query?.IsBulkOperation ?? false));

    public static TimeSpan Sum(IEnumerable<CapturedCommand> commands)
    {
        var total = TimeSpan.Zero;
        foreach (var c in commands)
        {
            total += c.Duration;
        }

        return total;
    }

    public static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms";

    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Best short description of what a command loads: "Order (by CustomerId)", "Customer", raw SQL start.</summary>
    public static string Describe(CapturedCommand c)
    {
        if (c.Query is { RootEntityShortName: { } root })
        {
            var filter = c.Query.KeyFilters.FirstOrDefault();
            return filter is null ? root : $"{root} by {filter.PropertyName}";
        }

        var shape = c.Shape;
        return shape.Length > 60 ? shape[..57] + "..." : shape;
    }

    /// <summary>Where the query was issued from, for titles: " at OrderService.cs:42" or "".</summary>
    public static string AtCallSite(CapturedCommand c)
        => c.CallSite is { } cs ? " at " + cs : string.Empty;

    /// <summary>
    /// Where a patch has to be applied: the innermost user frame with source, which is the call site unless
    /// <see cref="QueryShapeOptions.InfrastructurePrefixes"/> moved attribution up to a caller. The caller's line names the
    /// operation; only the frame that wrote the LINQ can be edited.
    /// </summary>
    public static CallSite? PatchSite(CapturedCommand c)
    {
        foreach (var frame in c.CallPath)
        {
            if (frame.FilePath is not null)
            {
                return frame;
            }
        }

        return c.CallSite is { FilePath: not null } site ? site : null;
    }

    /// <summary>Lower-camel identifier for a lambda parameter: Customer -> c, OrderLine -> ol.</summary>
    public static string LambdaName(string entityShortName)
    {
        var initials = new string(entityShortName.Where(char.IsUpper).Select(char.ToLowerInvariant).ToArray());
        return initials.Length == 0 ? "x" : initials;
    }

    /// <summary>Inserts <paramref name="operatorCall"/> right after the query root in a printed EF expression.</summary>
    public static string InsertAfterRoot(string expression, string operatorCall)
    {
        // EF prints roots as "DbSet<Customer>()" followed by newline-indented operators.
        var idx = expression.IndexOf("()", StringComparison.Ordinal);
        if (expression.StartsWith("DbSet<", StringComparison.Ordinal) && idx > 0)
        {
            return expression[..(idx + 2)] + "\n    ." + operatorCall + expression[(idx + 2)..];
        }

        return expression + "\n    ." + operatorCall;
    }

    public static IReadOnlyDictionary<string, string> Details(params (string Key, string Value)[] pairs)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }
}

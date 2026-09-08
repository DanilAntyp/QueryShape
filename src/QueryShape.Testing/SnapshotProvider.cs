namespace QueryShape.Testing;

/// <summary>Maps EF Core provider names to the short suffix used in snapshot file names (<c>sqlite</c>, <c>sqlserver</c>, <c>postgresql</c>...).</summary>
public static class SnapshotProvider
{
    /// <summary>Short, file-name-safe name for a provider, e.g. <c>Microsoft.EntityFrameworkCore.SqlServer</c> → <c>sqlserver</c>.</summary>
    public static string ShortName(string providerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        var name = providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "postgresql",
            "Pomelo.EntityFrameworkCore.MySql" or "MySql.EntityFrameworkCore" => "mysql",
            "Oracle.EntityFrameworkCore" => "oracle",
            "FirebirdSql.EntityFrameworkCore.Firebird" => "firebird",
            _ when providerName.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) => providerName["Microsoft.EntityFrameworkCore.".Length..],
            _ => providerName[(providerName.LastIndexOf('.') + 1)..],
        };

        return new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
    }

    /// <summary>The suffix for a scope: the distinct providers of its commands, sorted and joined with <c>+</c>; <c>null</c> when no provider is known.</summary>
    public static string? SuffixFor(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var names = scope.Commands
            .Select(c => c.ProviderName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => ShortName(p!))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? null : string.Join('+', names);
    }

    /// <summary>Inserts the provider suffix before the extension: <c>X.Y.json</c> → <c>X.Y.sqlite.json</c>.</summary>
    public static string WithSuffix(string path, string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        var ext = Path.GetExtension(path);
        return path[..^ext.Length] + "." + suffix + ext;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace QueryShape.Capture;

/// <summary>
/// Carries the <see cref="QueryShapeOptions"/> for a context inside its <see cref="DbContextOptions"/>, so a single interceptor
/// instance can serve every context without forcing EF Core to build a new internal service provider per options object.
/// </summary>
internal sealed class QueryShapeOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public QueryShapeOptionsExtension(QueryShapeOptions options)
    {
        Options = options;
    }

    public QueryShapeOptions Options { get; }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // Nothing to register: the interceptor is added through AddInterceptors.
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension)
            : base(extension)
        {
        }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using QueryShape ";

        // The options object never affects EF Core's internal services, so every instance can share one provider.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["QueryShape"] = "1";
    }
}

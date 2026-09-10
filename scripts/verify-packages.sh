#!/usr/bin/env bash
# Installs the freshly packed libraries into throwaway projects and uses them the way a consumer would:
# through NuGet, not project references, so a missing dependency or a broken target framework fails here
# rather than in someone else's build. Also installs the CLI as a global tool and runs it.
#
#   scripts/verify-packages.sh <artifacts-directory> <version>
set -euo pipefail

feed=$(cd "${1:?artifacts directory}" && pwd)
version="${2:?version}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

# One N+1, detected through the package: exercises capture, the scope, the rules and the fix generator.
cat > "$work/SmokeTests.cs" <<'EOF'
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryShape;
using Xunit;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
}

public class SmokeTests
{
    [Fact]
    public async Task Detects_an_n_plus_one_through_the_package()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).UseQueryShape().Options;

        await using var db = new ShopContext(options);
        await db.Database.EnsureCreatedAsync();
        for (var i = 1; i <= 6; i++)
        {
            db.Customers.Add(new Customer { Name = "C" + i, Orders = [new Order { Total = i }] });
        }

        await db.SaveChangesAsync();

        using var scope = QueryShapeScope.Begin("smoke");
        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            await db.Orders.AsNoTracking().Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var diagnoses = scope.Analyze();
        var nPlusOne = Assert.Single(diagnoses, d => d.RuleId == "QS001");
        Assert.Equal(Severity.Error, nPlusOne.Severity);
        Assert.Contains("Include", nPlusOne.SuggestedFix!.Summary);
    }
}
EOF

# net10.0 on the current EF Core 10 patch, and net8.0 on the floor of EF Core 8 that the libraries reference:
# a consumer pinned to that floor must not be forced into a downgrade or an upgrade.
verify_consumer() {
    local framework="$1" ef="$2" dir="$work/$1"
    mkdir -p "$dir"
    cp "$work/nuget.config" "$work/SmokeTests.cs" "$dir/"
    cat > "$dir/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$framework</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <!-- CI installs both runtimes, so this never rolls forward there; it lets the script also run on a machine that only has .NET 10,
         where the restore and compile checks still hold even though the net8.0 assembly then executes on .NET 10. -->
    <RollForward>Major</RollForward>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="QueryShape.Testing" Version="$version" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="$ef" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
EOF
    echo "==> consumer on $framework with EF Core $ef"
    dotnet test "$dir" --nologo --verbosity quiet
}

verify_consumer net10.0 10.0.11
verify_consumer net8.0 8.0.10

echo "==> dotnet tool"
dotnet tool install QueryShape.Cli --version "$version" --add-source "$feed" --tool-path "$work/tool" >/dev/null
"$work/tool/dotnet-queryshape" --help | grep -q "verify" || { echo "the CLI did not list its commands"; exit 1; }

echo "All packages verified for $version."

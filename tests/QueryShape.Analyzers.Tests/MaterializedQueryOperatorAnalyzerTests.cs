using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using QueryShape.Analyzers;

namespace QueryShape.Analyzers.Tests;

public class MaterializedQueryOperatorAnalyzerTests
{
    // A minimal stand-in for EF Core: DbSet<T> is an IQueryable<T>, ToListAsync is an extension on IQueryable<T>. No EF reference needed.
    private const string Stub = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.EntityFrameworkCore
        {
            public class DbSet<T> : IQueryable<T>
            {
                public Type ElementType => typeof(T);
                public Expression Expression => Expression.Constant(this);
                public IQueryProvider Provider => null!;
                public IEnumerator<T> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class EntityFrameworkQueryableExtensions
            {
                public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) => throw new NotImplementedException();
                public static Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            }
        }

        namespace Shop
        {
            using Microsoft.EntityFrameworkCore;

            public class Order { public int Id { get; set; } public decimal Total { get; set; } }
            public class ShopDb { public DbSet<Order> Orders { get; } = new DbSet<Order>(); }
        }
        """;

    private static Task VerifyAsync(string body)
    {
        var test = new CSharpAnalyzerTest<MaterializedQueryOperatorAnalyzer, DefaultVerifier>
        {
            TestCode = Stub + """

                namespace Shop
                {
                    using System.Linq;
                    using System.Threading.Tasks;
                    using Microsoft.EntityFrameworkCore;

                    public static class Usage
                    {
                        public static bool Helper(Order o) => o.Total > 1;
                        public static async Task Run(ShopDb db)
                        {
                """ + body + """

                        }
                    }
                }
                """,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        return test.RunAsync();
    }

    [Theory]
    [InlineData("var a = db.Orders.ToList().{|QSA001:Where|}(o => o.Total > 10);")]
    [InlineData("var b = (await db.Orders.ToListAsync()).{|QSA001:First|}(o => o.Id == 1);")]
    [InlineData("var c = db.Orders.ToArray().{|QSA001:Count|}(o => o.Total > 0);")]
    [InlineData("var d = (await db.Orders.ToArrayAsync()).{|QSA001:OrderBy|}(o => o.Id).Take(5).ToList();")]
    [InlineData("var e = db.Orders.Where(o => o.Total > 1).ToList().{|QSA001:Take|}(10);")]
    [InlineData("var f = db.Orders.ToList().{|QSA001:Sum|}(o => o.Total);")]
    public Task Reducing_operator_right_after_a_materializer_on_a_query_is_flagged(string body) => VerifyAsync(body);

    [Theory]
    [InlineData("var a = db.Orders.Where(o => o.Total > 10).ToList();")]                       // operator before the materializer
    [InlineData("var b = db.Orders.AsEnumerable().Where(o => Helper(o)).ToList();")]           // explicit client-evaluation boundary
    [InlineData("var c = db.Orders.ToList().Select(o => o.Id).ToList();")]                     // projection is not a reducing operator
    [InlineData("var l = db.Orders.ToList(); var d = l.Where(o => o.Total > 10).ToList();")]   // not a direct chain: the list may be reused
    [InlineData("var m = new List<Order>(); var e = m.ToList().Where(o => o.Total > 10).ToList();")] // source is not an EF Core query
    [InlineData("var f = db.Orders.ToList().Count;")]                                          // property, not the LINQ operator
    public Task Legitimate_shapes_are_not_flagged(string body) => VerifyAsync(body);
}

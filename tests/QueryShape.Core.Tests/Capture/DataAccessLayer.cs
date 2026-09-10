using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;

namespace QueryShape.Core.Tests.Infrastructure;

/// <summary>
/// Stands in for the data-access layer every real application has between its endpoints and EF Core: the frame that runs the query is
/// this repository, but the decision to ask for the data was made by whoever called it. Its namespace is what
/// <see cref="QueryShapeOptions.InfrastructurePrefixes"/> matches in the tests (a real app matches the assembly).
/// </summary>
internal sealed class CustomerRepository(ShopContext context)
{
    public Task<List<Customer>> ListAsync() => context.Customers.Include(c => c.Orders).ToListAsync();
}

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QueryShape.Testing.MSTest;

namespace QueryShape.Testing.MSTest.Tests;

[TestClass]
public class BudgetTests
{
    private MiniShop _shop = null!;

    [TestInitialize]
    public void Init() => _shop = new MiniShop();

    [TestCleanup]
    public void Cleanup() => _shop.Dispose();

    [QueryBudgetTestMethod(MaxQueries = 20, FailOn = Severity.Error)]
    public async Task Attribute_opens_a_scope_the_test_body_runs_in()
    {
        // MSTest runs [TestInitialize] inside ExecuteAsync, so the fixture's schema and seed commands are in the scope too; the budget above allows for them.
        Assert.IsNotNull(QueryShapeScope.Current, "ExecuteAsync opened the scope before invoking the test");
        var before = QueryShapeScope.Current!.CommandCount;
        using var ctx = _shop.Create();
        await ctx.Products.CountAsync();
        Assert.AreEqual(before + 1, QueryShapeScope.Current.CommandCount);
        StringAssert.Contains(QueryShapeScope.Current.Commands[^1].Shape, "COUNT");
    }

    [TestMethod]
    public async Task Exceeding_the_budget_marks_the_result_failed()
    {
        var attribute = new QueryBudgetTestMethodAttribute { MaxQueries = 1 };
        var results = await attribute.ExecuteAsync(new FakeTestMethod(this, nameof(RunsTwoQueries)));

        Assert.AreEqual(1, results.Length);
        Assert.AreEqual(UnitTestOutcome.Failed, results[0].Outcome);
        Assert.IsInstanceOfType<QueryBudgetExceededException>(results[0].TestFailureException);
        Assert.IsNull(QueryShapeScope.Current, "the attribute disposed its scope");
    }

    public async Task RunsTwoQueries()
    {
        using var ctx = _shop.Create();
        await ctx.Products.CountAsync();
        await ctx.Products.CountAsync();
    }

    /// <summary>What the MSTest adapter hands to TestMethodAttribute.ExecuteAsync, reduced to invoking one method on this instance.</summary>
    private sealed class FakeTestMethod(object instance, string name) : ITestMethod
    {
        public string TestMethodName => name;

        public string TestClassName => instance.GetType().FullName!;

        public Type ReturnType => MethodInfo.ReturnType;

        public object?[]? Arguments => null;

        public ParameterInfo[] ParameterTypes => MethodInfo.GetParameters();

        public MethodInfo MethodInfo => instance.GetType().GetMethod(name)!;

        public TestResult Invoke(object?[]? arguments) => InvokeAsync(arguments).GetAwaiter().GetResult();

        public async Task<TestResult> InvokeAsync(object?[]? arguments)
        {
            var result = MethodInfo.Invoke(instance, arguments);
            if (result is Task task)
            {
                await task;
            }

            return new TestResult { Outcome = UnitTestOutcome.Passed };
        }

        public Attribute[]? GetAllAttributes() => GetAllAttributes(inherit: true);

        public Attribute[]? GetAllAttributes(bool inherit) => MethodInfo.GetCustomAttributes(inherit).OfType<Attribute>().ToArray();

        public TAttributeType[] GetAttributes<TAttributeType>()
            where TAttributeType : Attribute
            => GetAttributes<TAttributeType>(inherit: true);

        public TAttributeType[] GetAttributes<TAttributeType>(bool inherit)
            where TAttributeType : Attribute
            => MethodInfo.GetCustomAttributes<TAttributeType>(inherit).ToArray();
    }
}

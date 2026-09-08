using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace QueryShape.Analyzers;

/// <summary>
/// QSA001: a reducing LINQ operator (Where, First, OrderBy, Take, Count...) applied directly to the result of <c>ToList()</c>/<c>ToArray()</c>
/// (or their async forms) on an EF Core query. The whole table was loaded so that LINQ-to-Objects could throw most of it away; moving the
/// operator before the materializer lets EF Core translate it to SQL. This is the compile-time half of QS003: at runtime EF Core never sees
/// those operators. <c>AsEnumerable()</c> is deliberately not flagged: it is the documented way to opt into client evaluation.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MaterializedQueryOperatorAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic id.</summary>
    public const string DiagnosticId = "QSA001";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        title: "LINQ operator runs in memory after the EF Core query was materialized",
        messageFormat: "'{0}' runs in memory because '{1}' already loaded every row of the EF Core query; move '{0}' before '{1}' so EF Core translates it to SQL",
        category: "QueryShape.Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ToList()/ToArray() executes the query and loads every row; a Where/First/OrderBy/Take/Count applied to that list is LINQ-to-Objects and never reaches EF Core. " +
                     "Put the operator before the materializer so the database does the filtering. Use AsEnumerable() when client evaluation is intended.",
        helpLinkUri: "https://github.com/DanilAntyp/QueryShape/blob/main/docs/rules/QSA001.md");

    private static readonly HashSet<string> s_materializers = new(System.StringComparer.Ordinal) { "ToList", "ToListAsync", "ToArray", "ToArrayAsync" };

    private static readonly HashSet<string> s_reducingOperators = new(System.StringComparer.Ordinal)
    {
        "Where", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
        "Any", "All", "Count", "LongCount", "Take", "Skip", "TakeWhile", "SkipWhile",
        "OrderBy", "OrderByDescending", "Min", "Max", "Sum", "Average", "MinBy", "MaxBy",
    };

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var enumerable = start.Compilation.GetTypeByMetadataName("System.Linq.Enumerable");
            var queryable = start.Compilation.GetTypeByMetadataName("System.Linq.IQueryable`1");
            if (enumerable is null || queryable is null)
            {
                return;
            }

            start.RegisterOperationAction(ctx => Analyze(ctx, enumerable, queryable), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol enumerable, INamedTypeSymbol queryableOfT)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!s_reducingOperators.Contains(method.Name) || !SymbolEqualityComparer.Default.Equals(method.ContainingType, enumerable))
        {
            return;
        }

        // Extension-method call: the receiver is the first argument.
        if (invocation.Arguments.Length == 0 || Unwrap(invocation.Arguments[0].Value) is not IInvocationOperation materializer)
        {
            return;
        }

        if (!s_materializers.Contains(materializer.TargetMethod.Name) || materializer.Arguments.Length == 0)
        {
            return;
        }

        var source = Unwrap(materializer.Arguments[0].Value);
        if (source?.Type is null || !ImplementsQueryable(source.Type, queryableOfT))
        {
            return;
        }

        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(s_rule, location, method.Name, materializer.TargetMethod.Name));
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IAwaitOperation await:
                    operation = await.Operation;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static bool ImplementsQueryable(ITypeSymbol type, INamedTypeSymbol queryableOfT)
    {
        if (type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, queryableOfT))
        {
            return true;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, queryableOfT))
            {
                return true;
            }
        }

        return false;
    }
}

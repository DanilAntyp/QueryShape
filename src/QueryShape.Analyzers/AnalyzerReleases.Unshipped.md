; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QSA001 | QueryShape.Performance | Warning | LINQ operator applied right after ToList()/ToArray() on an EF Core query runs in memory

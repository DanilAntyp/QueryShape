namespace QueryShape;

/// <summary>The first user-code frame that caused a query to run.</summary>
/// <param name="FilePath">Full path of the source file, or <c>null</c> when no portable PDB was available.</param>
/// <param name="Line">1-based line number, or 0 when unknown.</param>
/// <param name="Member">Type and member name, e.g. <c>OrderService.GetOrdersAsync</c>; empty when only file and line are known (EF Core's <c>TagWithCallSite</c>).</param>
public sealed record CallSite(string? FilePath, int Line, string Member)
{
    /// <summary>File name without directory, or <c>null</c>.</summary>
    public string? FileName => FilePath is null ? null : Path.GetFileName(FilePath);

    /// <summary>The member when known, else <c>File.cs:42</c>: something to name the site by in titles.</summary>
    public string Label
        => Member.Length > 0 ? Member
            : FileName is null ? "unknown call site"
            : Line > 0 ? $"{FileName}:{Line}" : FileName;

    /// <summary>Renders as <c>File.cs:42 Type.Member</c> (<c>File.cs:42</c> when the member is unknown, <c>Type.Member</c> when the file is).</summary>
    public override string ToString()
        => FileName is null
            ? Member
            : Line > 0
                ? Member.Length > 0 ? $"{FileName}:{Line} {Member}" : $"{FileName}:{Line}"
                : Member.Length > 0 ? $"{FileName} {Member}" : FileName;
}

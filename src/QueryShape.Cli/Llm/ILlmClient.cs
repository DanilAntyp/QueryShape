namespace QueryShape.Cli.Llm;

/// <summary>Thin seam over an LLM provider. Only ever used from the CLI, behind --llm.</summary>
internal interface ILlmClient
{
    string Model { get; }

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}

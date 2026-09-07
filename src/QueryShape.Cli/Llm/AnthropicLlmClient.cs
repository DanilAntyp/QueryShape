using Anthropic;
using Anthropic.Models.Messages;

namespace QueryShape.Cli.Llm;

/// <summary>Anthropic Messages API through the official SDK. The key comes from ANTHROPIC_API_KEY only.</summary>
internal sealed class AnthropicLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    public const string DefaultModel = "claude-opus-5";

    private readonly AnthropicClient _client;

    public AnthropicLlmClient(string apiKey, string? model)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    public string Model { get; }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 4096,
            System = systemPrompt,
            Messages = [new() { Role = Role.User, Content = userPrompt }],
        }, cancellationToken: ct);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"(no text returned; stop reason: {response.StopReason})";
        }

        return text;
    }
}

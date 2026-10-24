using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SupportBot;

/// <summary>
/// Stands in for a real provider (OpenAI, Azure OpenAI, Anthropic, Ollama...) so the sample records without an API
/// key. It asks for search_orders when the question mentions an order, and counts about four characters per token.
/// In your project this is your real chat client; TokenLedger records it the same way.
/// </summary>
public sealed partial class DemoModel : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var last = list[^1];
        ChatMessage answer;
        if (last.Role == ChatRole.Tool)
        {
            var result = last.Contents.OfType<FunctionResultContent>().First().Result?.ToString();
            answer = new ChatMessage(ChatRole.Assistant, $"{result} Anything else I can help with?");
        }
        else if (OrderNumber().Match(last.Text) is { Success: true } order)
        {
            answer = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call_{Guid.NewGuid():N}", "search_orders", new Dictionary<string, object?> { ["orderId"] = int.Parse(order.Groups[1].Value) })]);
        }
        else
        {
            answer = new ChatMessage(ChatRole.Assistant, "Hello! I can help with your orders. What is your order number?");
        }

        var input = list.Sum(m => m.Text.Length + 16) / 4 + 50;
        var output = Math.Max(answer.Text.Length / 4, 12);
        return Task.FromResult(new ChatResponse(answer)
        {
            ModelId = "demo-model-2026-06-01",
            FinishReason = answer.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents) { ModelId = response.ModelId };
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { FinishReason = response.FinishReason };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    [GeneratedRegex(@"order\s+(?:number\s+)?#?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex OrderNumber();
}

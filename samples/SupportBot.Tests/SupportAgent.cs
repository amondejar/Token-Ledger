using Microsoft.Extensions.AI;

namespace SupportBot;

/// <summary>The code under test: answers customer questions, looking orders up when the model asks to.</summary>
public sealed class SupportAgent(IChatClient chat)
{
    private const string Instructions = "You are the support assistant of an online shop. Be brief. Use search_orders to look up an order.";

    private static readonly Dictionary<int, string> Orders = new() { [42] = "shipped on Monday, arriving Thursday", [7] = "waiting for payment" };

    public async Task<string> AnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, Instructions), new(ChatRole.User, question) };
        for (var round = 0; round < 5; round++)
        {
            var response = await chat.GetResponseAsync(messages, new ChatOptions { ModelId = "demo-model" }, cancellationToken);
            messages.AddRange(response.Messages);

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0) return response.Text;

            // Run the tools the model asked for and send their results back.
            messages.Add(new ChatMessage(ChatRole.Tool, [.. calls.Select(c => (AIContent)new FunctionResultContent(c.CallId, SearchOrders(c.Arguments)))]));
        }

        return "Sorry, I could not answer that.";
    }

    private static string SearchOrders(IDictionary<string, object?>? arguments)
    {
        var id = int.TryParse(arguments?["orderId"]?.ToString(), out var n) ? n : 0;
        return Orders.TryGetValue(id, out var status) ? $"Order {id}: {status}." : $"Order {id} not found.";
    }
}

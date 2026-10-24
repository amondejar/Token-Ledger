using Microsoft.Extensions.AI;
using SupportBot;
using TokenLedger;

// The app's pipeline: TokenLedger last, closest to the provider.
IChatClient chat = new DemoModel().AsBuilder().UseTokenLedger().Build();
var agent = new SupportAgent(chat);

var failed = 0;
await Scenario("Order status: one lookup, then the answer", async () =>
{
    await using var ledger = Ledger.Scenario("support/order-status");
    var answer = await agent.AnswerAsync("Where is my order 42?");

    if (!answer.Contains("arriving Thursday")) throw new Exception($"unexpected answer: {answer}");
    ledger.Budget(maxCalls: 2, maxTokens: 1_000);
});

await Scenario("Greeting: no tools", async () =>
{
    await using var ledger = Ledger.Scenario("support/greeting");
    var answer = await agent.AnswerAsync("Hi there!");

    if (!answer.Contains("order number")) throw new Exception($"unexpected answer: {answer}");
    ledger.Budget(maxCalls: 1);
});

return failed == 0 ? 0 : 1;

async Task Scenario(string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}\n        {e.Message.ReplaceLineEndings("\n        ")}");
    }
}

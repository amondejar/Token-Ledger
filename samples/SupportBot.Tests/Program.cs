using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SupportBot;
using TokenLedger;
using TokenLedger.AspNetCore;

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

await Scenario("Over HTTP: the cost of POST api/support/chat", async () =>
{
    // The application under test: the same agent behind an endpoint, with TokenLedger's middleware.
    var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=None"]);
    builder.Services.AddTokenLedger();
    builder.Services.AddSingleton(agent);
    await using var app = builder.Build();
    app.MapPost("/api/support/chat", async (ChatRequest request, SupportAgent support) => new ChatReply(await support.AnswerAsync(request.Message)));
    await app.StartAsync();

    // The test: requests sent through LedgerHttpHandler are measured in the scenario, per endpoint.
    await using var ledger = Ledger.Scenario("api/support-chat");
    using var client = new HttpClient(new LedgerHttpHandler(ledger, new SocketsHttpHandler())) { BaseAddress = new Uri(app.Urls.First()) };
    var reply = await client.PostAsJsonAsync("/api/support/chat", new ChatRequest("Where is my order 7?"));
    var answer = (await reply.Content.ReadFromJsonAsync<ChatReply>())!.Answer;

    if (!answer.Contains("waiting for payment")) throw new Exception($"unexpected answer: {answer}");
    ledger.Budget(maxCalls: 2);
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

internal sealed record ChatRequest(string Message);

internal sealed record ChatReply(string Answer);

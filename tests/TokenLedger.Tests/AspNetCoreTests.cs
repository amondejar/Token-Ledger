using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TokenLedger.AspNetCore;
using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

/// <summary>TokenLedger.AspNetCore against a real application on a local port.</summary>
internal static class AspNetCoreTests
{
    /// <summary>An API whose endpoints call the model: GET chat/{topic} twice, POST summaries once.</summary>
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=None"]);
        builder.Services.AddTokenLedger();
        builder.Services.AddSingleton<IChatClient>(new TokenLedgerChatClient(new FakeModel(m => FakeModel.Text($"answer to {m[^1].Text}"))));
        var app = builder.Build();

        app.MapGet("/chat/{topic}", async (string topic, IChatClient chat) =>
        {
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, $"first question about {topic}")]);
            return (await chat.GetResponseAsync([new ChatMessage(ChatRole.User, $"second question about {topic}")])).Text;
        });
        app.MapPost("/summaries", async (IChatClient chat) => (await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "summarize")])).Text);

        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, LedgerScenario? scenario) =>
        new(scenario is null ? new SocketsHttpHandler() : new LedgerHttpHandler(scenario, new SocketsHttpHandler()))
        {
            BaseAddress = new Uri(app.Urls.First()),
        };

    public static void Register()
    {
        Test("ASP.NET Core: calls are attributed to the endpoint's route, not its path", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            await using var app = await StartAsync();

            var ledger = Ledger.Scenario("api/chat");
            using (var client = Client(app, ledger))
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/chat/orders")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/chat/billing")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/summaries", null)).StatusCode);
            }

            ledger.Verify();

            Assert.Equal("GET chat/{topic},GET chat/{topic},GET chat/{topic},GET chat/{topic},POST summaries", string.Join(",", ledger.Calls.Select(c => c.Endpoint)));
            var baseline = File.ReadAllText(sandbox.Baseline("api/chat"));
            Assert.Contains("\"endpoint\":\"POST summaries\"", baseline);
            Assert.Contains("\n  \"endpoints\": {\n    \"GET chat/{topic}\": {\"calls\":4,\"input\":40,", baseline);
            Assert.Contains("\"POST summaries\": {\"calls\":1,", baseline);
        });

        Test("ASP.NET Core: without the handler a request on a real host is not part of the scenario", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            await using var app = await StartAsync();

            using var ledger = Ledger.Scenario("api/unrelated");
            using var client = Client(app, scenario: null);
            await client.PostAsync("/summaries", null);

            Assert.Equal(0, ledger.Calls.Count);
        });

        Test("ASP.NET Core: scenarios running in parallel keep their own calls", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            await using var app = await StartAsync();

            async Task<LedgerScenario> Run(string name, string path, int times)
            {
                await Task.Yield();
                var ledger = Ledger.Scenario(name);
                using var client = Client(app, ledger);
                for (var i = 0; i < times; i++) await client.GetAsync(path);
                ledger.Verify();
                return ledger;
            }

            var results = await Task.WhenAll(Run("parallel/a", "/chat/a", 3), Run("parallel/b", "/chat/b", 1));

            Assert.Equal(6, results[0].Calls.Count);
            Assert.Equal(2, results[1].Calls.Count);
        });

        Test("ASP.NET Core: more calls from one endpoint are reported under its name", () =>
        {
            var before = Baselines.Build("x", [Call("POST summaries"), Call("GET chat/{topic}")]);
            var after = Baselines.Build("x", [Call("POST summaries"), Call("POST summaries"), Call("GET chat/{topic}")]);

            var differences = Baselines.Compare(before, after, 0.5).Select(d => d.ToString()).ToList();

            Assert.Contains("regression  POST summaries: model calls 1 → 2", string.Join("|", differences));
            Assert.DoesNotContain("GET chat/{topic}:", string.Join("|", differences));
            return Task.CompletedTask;

            static LedgerCall Call(string endpoint) => new() { Index = 1, Model = "m", InputTokens = 10, OutputTokens = 1, Endpoint = endpoint };
        });

        Test("ASP.NET Core: baselines without endpoints are unchanged", () =>
        {
            var baseline = Baselines.Serialize(Baselines.Build("x", [new LedgerCall { Index = 1, Model = "m", InputTokens = 1, OutputTokens = 1 }]));
            Assert.DoesNotContain("endpoint", baseline);
            return Task.CompletedTask;
        });
    }
}

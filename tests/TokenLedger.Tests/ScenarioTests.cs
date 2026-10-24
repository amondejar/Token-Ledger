using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

internal static class ScenarioTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Where is my order 42?")];

    private static string CallerPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    public static void Register()
    {
        Test("Outside a scenario the middleware passes calls through and writes nothing", async () =>
        {
            using var sandbox = new Sandbox();
            var model = new FakeModel(_ => FakeModel.Text("On its way."));
            var client = new TokenLedgerChatClient(model);

            var response = await client.GetResponseAsync(Question);

            Assert.Equal("On its way.", response.Text);
            Assert.Equal(1, model.Calls);
            Assert.True(!Directory.Exists(sandbox.Directory));
            Assert.True(Ledger.Current is null);
        });

        Test("Auto records on the first run and replays on the next without calling the model", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            var model = new FakeModel(_ => FakeModel.Text("On its way.", "gpt-test-2026-01-01"));
            var client = new TokenLedgerChatClient(model);

            await using (var first = Ledger.Scenario("orders/status"))
            {
                Assert.Equal("On its way.", (await client.GetResponseAsync(Question)).Text);
                Assert.Equal(false, first.Calls[0].Replayed);
            }

            Assert.True(File.Exists(sandbox.Recording("orders/status")));
            Assert.True(File.Exists(sandbox.Baseline("orders/status")));

            LedgerScenario second;
            await using (second = Ledger.Scenario("orders/status"))
            {
                var replayed = await client.GetResponseAsync(Question);
                Assert.Equal("On its way.", replayed.Text);
                Assert.Equal("gpt-test-2026-01-01", replayed.ModelId);
                Assert.Equal(10L, replayed.Usage?.InputTokenCount);
            }

            Assert.Equal(1, model.Calls);
            Assert.Equal(true, second.Calls[0].Replayed);
            Assert.Equal(0, second.Differences.Count);
            Assert.True(Ledger.Current is null);
        });

        Test("Replay fails a request without a recording, in the call and in Verify", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Replay);
            var model = new FakeModel(_ => FakeModel.Text("On its way."));
            var client = new TokenLedgerChatClient(model);

            var scenario = Ledger.Scenario("orders/missing");
            var inCall = await Assert.ThrowsAsync<TokenLedgerException>(() => client.GetResponseAsync(Question));
            Assert.Contains("no recorded response", inCall.Message);
            Assert.Contains("user: Where is my order 42?", inCall.Message);

            var atEnd = Assert.Throws<TokenLedgerException>(scenario.Verify);
            Assert.Contains("1 model call(s) had no recording in", atEnd.Message);
            Assert.Equal(0, model.Calls);
            Assert.True(!File.Exists(sandbox.Baseline("orders/missing")));   // incomplete numbers never become a baseline
        });

        Test("Record calls the model every time and rewrites the recording", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Record);
            var answers = 0;
            var model = new FakeModel(_ => FakeModel.Text($"Answer {++answers}"));
            var client = new TokenLedgerChatClient(model);
            Ledger.Options.FailOn = LedgerFailOn.Never;

            await using (Ledger.Scenario("orders/record")) await client.GetResponseAsync(Question);
            await using (Ledger.Scenario("orders/record")) await client.GetResponseAsync(Question);

            Assert.Equal(2, model.Calls);
            var recording = File.ReadAllText(sandbox.Recording("orders/record"));
            Assert.Contains("Answer 2", recording);
            Assert.DoesNotContain("Answer 1", recording);
        });

        Test("Live measures real calls without touching recordings", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var model = new FakeModel(_ => FakeModel.Text("On its way."));
            var client = new TokenLedgerChatClient(model);

            LedgerScenario scenario;
            await using (scenario = Ledger.Scenario("orders/live")) await client.GetResponseAsync(Question);

            Assert.Equal(1, scenario.Totals.Calls);
            Assert.True(!File.Exists(sandbox.Recording("orders/live")));
            Assert.True(File.Exists(sandbox.Baseline("orders/live")));
        });

        Test("A tool loop is recorded per round trip and replayed with the tools in the baseline", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            var callIds = 0;
            var model = new FakeModel(
                _ => FakeModel.Call("get_order", $"call_{++callIds}", new() { ["id"] = 42 }),
                messages => FakeModel.Text($"Order 42 is {((FunctionResultContent)messages[^1].Contents[0]).Result}."));
            var client = new TokenLedgerChatClient(model);

            async Task<string> Run()
            {
                var messages = Question.ToList();
                var first = await client.GetResponseAsync(messages);
                var call = first.Messages[0].Contents.OfType<FunctionCallContent>().Single();
                messages.AddRange(first.Messages);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "shipped")]));
                return (await client.GetResponseAsync(messages)).Text;
            }

            await using (Ledger.Scenario("orders/tools")) Assert.Equal("Order 42 is shipped.", await Run());
            LedgerScenario replay;
            await using (replay = Ledger.Scenario("orders/tools")) Assert.Equal("Order 42 is shipped.", await Run());

            Assert.Equal(2, model.Calls);
            Assert.Equal("get_order", string.Join(",", replay.Calls.SelectMany(c => c.Tools)));
            Assert.True(replay.Calls.All(c => c.Replayed));
            Assert.Contains("\"tools\":[\"get_order\"]", File.ReadAllText(sandbox.Baseline("orders/tools")));
        });

        Test("The same request more times than recorded gets the last recorded answer", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            var model = new FakeModel(_ => FakeModel.Text("Once"), _ => FakeModel.Text("Twice"));
            var client = new TokenLedgerChatClient(model);

            await using (Ledger.Scenario("orders/repeat")) await client.GetResponseAsync(Question);

            Sandbox.Mode = LedgerMode.Replay;
            Ledger.Options.FailOn = LedgerFailOn.Never;
            await using var replay = Ledger.Scenario("orders/repeat");
            Assert.Equal("Once", (await client.GetResponseAsync(Question)).Text);
            Assert.Equal("Once", (await client.GetResponseAsync(Question)).Text);
            Assert.Equal(1, model.Calls);
        });

        Test("Normalizers let a request with variable data replay", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            Ledger.Options.NormalizeTimestamps().NormalizeGuids();
            var model = new FakeModel(_ => FakeModel.Text("Noted."));
            var client = new TokenLedgerChatClient(model);

            ChatMessage[] Ask(string when, Guid id) => [new(ChatRole.User, $"Log ticket {id} opened at {when}")];

            await using (Ledger.Scenario("tickets/log")) await client.GetResponseAsync(Ask("2026-10-24T07:47:00Z", Guid.NewGuid()));
            Sandbox.Mode = LedgerMode.Replay;
            await using (Ledger.Scenario("tickets/log")) await client.GetResponseAsync(Ask("2026-10-25T09:12:31.5+02:00", Guid.NewGuid()));

            Assert.Equal(1, model.Calls);
        });

        Test("Redactors keep secrets out of the recording", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            Ledger.Options.Redact(new Regex(@"\b\d{4} \d{4} \d{4} \d{4}\b"), "<card>");
            var model = new FakeModel(_ => FakeModel.Text("Charged card 4111 1111 1111 1111."));
            var client = new TokenLedgerChatClient(model);

            await using (Ledger.Scenario("payments/charge"))
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Charge 4111 1111 1111 1111")]);

            var recording = File.ReadAllText(sandbox.Recording("payments/charge"));
            Assert.DoesNotContain("4111", recording);
            Assert.Contains("Charged card <card>.", recording);
            Assert.Contains("user: Charge <card>", recording);
        });

        Test("Streaming is recorded with its usage and replays as a stream or as a response", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            var model = new FakeModel(_ => FakeModel.Text("Your order ships tomorrow.", "gpt-test"));
            var client = new TokenLedgerChatClient(model);

            async Task<string> Stream()
            {
                var text = "";
                await foreach (var update in client.GetStreamingResponseAsync(Question)) text += update.Text;
                return text;
            }

            LedgerScenario recorded;
            await using (recorded = Ledger.Scenario("orders/stream")) Assert.Equal("Your order ships tomorrow.", await Stream());
            Assert.Equal(10L, recorded.Calls[0].InputTokens);
            Assert.Equal(26L, recorded.Calls[0].OutputTokens);
            Assert.Equal(true, recorded.Calls[0].Streaming);

            Sandbox.Mode = LedgerMode.Replay;
            await using (Ledger.Scenario("orders/stream"))
            {
                Assert.Equal("Your order ships tomorrow.", await Stream());
            }

            Ledger.Options.FailOn = LedgerFailOn.Never;
            await using (var asResponse = Ledger.Scenario("orders/stream"))
            {
                var response = await client.GetResponseAsync(Question);
                Assert.Equal("Your order ships tomorrow.", response.Text);
                Assert.Equal(26L, asResponse.Totals.OutputTokens);
            }

            Assert.Equal(1, model.Calls);
        });

        Test("The model falls back to the request's and then the client's default", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var client = new TokenLedgerChatClient(new FakeModel(_ => FakeModel.Text("ok")) { DefaultModelId = "client-default" });

            await using var scenario = Ledger.Scenario("models/fallback");
            await client.GetResponseAsync(Question);
            await client.GetResponseAsync(Question, new ChatOptions { ModelId = "from-options" });

            Assert.Equal("client-default,from-options", string.Join(",", scenario.Calls.Select(c => c.Model)));
        });

        Test("UseTokenLedger adds the middleware to a ChatClientBuilder", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var client = new FakeModel(_ => FakeModel.Text("ok")).AsBuilder().UseTokenLedger().Build();

            Assert.True(client is TokenLedgerChatClient);
            await using var scenario = Ledger.Scenario("builder/use");
            await client.GetResponseAsync(Question);
            Assert.Equal(1, scenario.Totals.Calls);
        });

        Test("The folder defaults to tokenledger in the test project, also when the build maps source paths", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Ledger.Options.Directory = null;
            var expected = Path.Combine(PublicApiTests.RepositoryRoot(), "tests", "TokenLedger.Tests", "tokenledger", "baselines", "paths", "default.json");

            foreach (var caller in new[] { CallerPath(), "/_/tests/TokenLedger.Tests/ScenarioTests.cs" })
            {
                var scenario = Ledger.Scenario("paths/default", caller);
                Ledger.Restore(scenario, null);   // not verified: it would write into the test project
                Assert.Equal(expected, scenario.BaselinePath);
            }
        });

        Test("Scenario names are checked", () =>
        {
            using var sandbox = new Sandbox();
            foreach (var bad in new[] { "Orders", "../escape", "a//b", "a/", "/a", "a b" })
                Assert.Throws<ArgumentException>(() => Ledger.Scenario(bad));
            Assert.True(Ledger.Current is null);
        });

        Test("Scenarios nest and restore the outer one", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var outer = Ledger.Scenario("nest/outer");
            using (var inner = Ledger.Scenario("nest/inner")) Assert.True(ReferenceEquals(Ledger.Current, inner));
            Assert.True(ReferenceEquals(Ledger.Current, outer));
            outer.Dispose();
            Assert.True(Ledger.Current is null);
        });

        Test("TOKENLEDGER_MODE wins over the options", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            Environment.SetEnvironmentVariable("TOKENLEDGER_MODE", "live");
            try
            {
                using var scenario = Ledger.Scenario("env/mode");
                Assert.Equal(LedgerMode.Live, scenario.Mode);
                Environment.SetEnvironmentVariable("TOKENLEDGER_MODE", "sometimes");
                Assert.Throws<TokenLedgerException>(() => Ledger.Scenario("env/bad"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TOKENLEDGER_MODE", null);
            }
        });

        Test("The recording is readable JSON with the request summary", async () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Auto);
            var client = new TokenLedgerChatClient(new FakeModel(_ => FakeModel.Text("¿Mañana?", "gpt-test")));
            await using (Ledger.Scenario("files/recording")) await client.GetResponseAsync(Question, new ChatOptions { ModelId = "gpt-test" });

            var text = File.ReadAllText(sandbox.Recording("files/recording"));
            Assert.Contains("¿Mañana?", text);   // not escaped
            using var json = JsonDocument.Parse(text);
            var interaction = json.RootElement.GetProperty("interactions")[0];
            Assert.Equal(32, interaction.GetProperty("fingerprint").GetString()!.Length);
            Assert.Equal("gpt-test", interaction.GetProperty("request").GetProperty("model").GetString());
            Assert.Equal("user: Where is my order 42?", interaction.GetProperty("request").GetProperty("last").GetString());
            Assert.Equal(10, interaction.GetProperty("response").GetProperty("usage").GetProperty("input").GetInt32());
        });
    }
}

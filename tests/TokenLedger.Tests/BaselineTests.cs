using Microsoft.Extensions.AI;
using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

internal static class BaselineTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Summarize my orders")];

    /// <summary>Runs a scenario live with a model that answers <paramref name="answers"/>, one call per answer.</summary>
    private static LedgerScenario Run(string scenario, params string[] answers) => Run(scenario, configure: null, answers);

    private static LedgerScenario Run(string scenario, Action<LedgerScenario>? configure, params string[] answers)
    {
        var client = new TokenLedgerChatClient(new FakeModel([.. answers.Select(a => (Func<IReadOnlyList<ChatMessage>, ChatResponse>)(_ => FakeModel.Text(a)))]));
        var ledger = Ledger.Scenario(scenario);
        try
        {
            configure?.Invoke(ledger);
            foreach (var _ in answers) client.GetResponseAsync(Question).GetAwaiter().GetResult();
        }
        catch
        {
            Ledger.Restore(ledger, null);
            throw;
        }

        ledger.Dispose();
        return ledger;
    }

    public static void Register()
    {
        Test("The first run creates the baseline with one call per line", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/first", "abc", "defgh");

            Assert.Equal("""
                {
                  "format": 1,
                  "scenario": "summary/first",
                  "calls": [
                    {"model":"fake-model","input":10,"output":3,"tools":[]},
                    {"model":"fake-model","input":10,"output":5,"tools":[]}
                  ],
                  "totals": {"calls":2,"input":20,"output":8,"cachedInput":0,"reasoning":0}
                }

                """.ReplaceLineEndings("\n"), File.ReadAllText(sandbox.Baseline("summary/first")));
        });

        Test("A missing baseline fails in CI instead of being created", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live, ci: true);
            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/ci", "abc"));
            Assert.Contains("No baseline at", error.Message);
            Assert.True(!File.Exists(sandbox.Baseline("summary/ci")));
        });

        Test("More calls are a regression and fail with the difference and how to accept it", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/calls", "abc");

            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/calls", "abc", "abc"));
            Assert.Contains("regression  model calls 1 → 2", error.Message);
            Assert.Contains("regression  input tokens 10 → 20 (+100 %)", error.Message);
            Assert.Contains("TOKENLEDGER_UPDATE=1", error.Message);
        });

        Test("Token changes within the tolerance pass; beyond it they fail", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/tokens", new string('x', 100));

            var within = Run("summary/tokens", new string('x', 109));
            Assert.Equal(0, within.Differences.Count);

            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/tokens", new string('x', 120)));
            Assert.Contains("regression  output tokens 100 → 120 (+20 %)", error.Message);

            Ledger.Options.Tolerance = 0.25;
            Assert.Equal(0, Run("summary/tokens", new string('x', 120)).Differences.Count);
        });

        Test("FailOn Regression lets improvements and behavior changes pass, but reports them", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/better", new string('x', 100));
            Ledger.Options.FailOn = LedgerFailOn.Regression;

            var cheaper = Run("summary/better", new string('x', 50));

            Assert.Equal(1, cheaper.Differences.Count);
            Assert.Equal(LedgerChangeKind.Improvement, cheaper.Differences[0].Kind);
            Assert.Equal("improvement output tokens 100 → 50 (-50 %)", cheaper.Differences[0].ToString());
        });

        Test("FailOn Change fails an improvement too, so the baseline stays current", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/strict", new string('x', 100));
            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/strict", new string('x', 50)));
            Assert.Contains("improvement output tokens 100 → 50", error.Message);
        });

        Test("A different tool sequence or model is a behavior change", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var before = new BaselineFile
            {
                Calls = [new() { Model = "gpt-a", Input = 10, Output = 1, Tools = ["search"] }],
                Totals = new() { Calls = 1, Input = 10, Output = 1 },
            };
            var after = new BaselineFile
            {
                Calls = [new() { Model = "gpt-b", Input = 10, Output = 1, Tools = ["search", "get_customer"] }],
                Totals = new() { Calls = 1, Input = 10, Output = 1 },
            };

            var differences = Baselines.Compare(before, after, 0.1).Select(d => d.ToString()).ToList();

            Assert.Equal("change      models: gpt-a → gpt-b|change      tools: search → search, get_customer", string.Join("|", differences));
        });

        Test("UpdateBaselines accepts the change and rewrites the baseline", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/update", "abc");
            Ledger.Options.UpdateBaselines = true;

            Run("summary/update", "abc", "abc");

            Assert.Contains("\"calls\":2", File.ReadAllText(sandbox.Baseline("summary/update")));
        });

        Test("The failure shows the cost change when every model has a price", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Ledger.Options.Pricing.Currency = "EUR";
            Ledger.Options.Pricing.Add("fake-model", input: 2m, output: 8m);
            Run("summary/cost", "abc");

            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/cost", "abc", "abc"));

            Assert.Contains("estimated cost 0.000044 EUR → 0.000088 EUR per run", error.Message);
        });

        Test("Calls without usage data are kept apart and flagged as a change", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("summary/usage", "abc");

            var client = new TokenLedgerChatClient(new FakeModel(_ => FakeModel.Text("abc")) { ReportUsage = false });
            var scenario = Ledger.Scenario("summary/usage");
            client.GetResponseAsync(Question).GetAwaiter().GetResult();
            var error = Assert.Throws<TokenLedgerException>(scenario.Verify);

            Assert.Equal(false, scenario.Calls[0].UsageReported);
            Assert.Contains("change      calls without usage data 0 → 1", error.Message);
        });

        Test("Budgets fail the scenario with every limit exceeded", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var error = Assert.Throws<TokenLedgerException>(() =>
                Run("budget/over", s => s.Budget(maxCalls: 1, maxTokens: 15), "abc", "abc"));

            Assert.Contains("Over budget: model calls 2 > 1; tokens 26 > 15.", error.Message);
        });

        Test("Budgets within limits pass, and the default budget applies to every scenario", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Run("budget/within", s => s.Budget(maxCalls: 2, maxInputTokens: 20, maxOutputTokens: 6), "abc", "abc");

            Ledger.Options.DefaultBudget = new LedgerBudget { MaxCalls = 1 };
            var error = Assert.Throws<TokenLedgerException>(() => Run("budget/default", "abc", "abc"));
            Assert.Contains("model calls 2 > 1", error.Message);
        });

        Test("A cost budget needs a price for every model", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            var error = Assert.Throws<TokenLedgerException>(() => Run("budget/unpriced", s => s.Budget(maxCost: 1m), "abc"));
            Assert.Contains("cost cannot be checked: no price for fake-model", error.Message);

            Ledger.Options.Pricing.Add("fake-model", input: 1_000m, output: 1_000m);
            Run("budget/priced", s => s.Budget(maxCost: 0.05m), "abc");
            error = Assert.Throws<TokenLedgerException>(() => Run("budget/expensive", s => s.Budget(maxCost: 0.01m), "abc"));
            Assert.Contains("estimated cost 0.013 USD > 0.01 USD", error.Message);
        });

        Test("A corrupt baseline is reported with what to do", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Directory.CreateDirectory(Path.GetDirectoryName(sandbox.Baseline("summary/corrupt"))!);
            File.WriteAllText(sandbox.Baseline("summary/corrupt"), "{ not json");

            var error = Assert.Throws<TokenLedgerException>(() => Run("summary/corrupt", "abc"));
            Assert.Contains("is not a valid baseline", error.Message);
        });
    }
}

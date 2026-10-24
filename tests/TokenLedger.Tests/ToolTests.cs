using System.Text.Json;
using TokenLedger.Tool;
using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

/// <summary>dotnet tokenledger, run in temporary git repositories.</summary>
internal static class ToolTests
{
    public static void Register()
    {
        Test("diff lists regressions between two refs and fails on them when asked", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tests/Shop.Tests/tokenledger", "support/chat", Calls(("gpt-4.1", 1000, 100, [])));
            repo.Commit("v1");
            repo.Baseline("tests/Shop.Tests/tokenledger", "support/chat", Calls(("gpt-4.1", 1000, 100, ["search"]), ("gpt-4.1", 1500, 120, [])));
            repo.Commit("v2");

            var (code, text) = repo.Tool("diff", "v1", "v2");
            Assert.Equal(Cli.Ok, code);
            Assert.Contains("TokenLedger: v1 → v2", text);
            Assert.Contains("1 regression, 0 changes, 0 improvements, 0 new, 0 removed, 0 unchanged", text);
            Assert.Contains("support/chat  regression", text);
            Assert.Contains("regression  model calls 1 → 2", text);
            Assert.Contains("change      tools: none → search", text);

            Assert.Equal(Cli.ChangesFound, repo.Tool("diff", "v1", "v2", "--fail-on", "regression").Code);
            Assert.Equal(Cli.Ok, repo.Tool("diff", "v2", "v1", "--fail-on", "regression").Code);   // the other way it is cheaper
            Assert.Equal(Cli.ChangesFound, repo.Tool("diff", "v2", "v1", "--fail-on", "change").Code);
        });

        Test("diff with no arguments compares HEAD with the working tree, new files included", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "a", Calls(("m", 100, 10, [])));
            repo.Commit("first");
            repo.Baseline("tokenledger", "a", Calls(("m", 50, 10, [])));
            repo.Baseline("tokenledger", "b", Calls(("m", 10, 1, [])));

            var (_, text) = repo.Tool("diff");

            Assert.Contains("TokenLedger: HEAD → working tree", text);
            Assert.Contains("a  improvement", text);
            Assert.Contains("improvement input tokens 100 → 50 (-50 %)", text);
            Assert.Contains("b  new", text);
            Assert.Contains("1 model call, 11 tokens", text);
        });

        Test("diff reports removed scenarios and an unchanged repository", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "gone", Calls(("m", 1, 1, [])));
            repo.Baseline("tokenledger", "kept", Calls(("m", 1, 1, [])));
            repo.Commit("first");
            Assert.Contains("No changes in 2 scenarios.", repo.Tool("diff").Output);

            File.Delete(Path.Combine(repo.Root, "tokenledger/baselines/gone.json"));
            var (_, text) = repo.Tool("diff");
            Assert.Contains("gone  removed", text);
            Assert.Contains("was 1 model call, 2 tokens", text);
        });

        Test("diff prices both sides with today's pricing.json and totals the suite", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "chat", Calls(("gpt-4.1-2025-04-14", 1_000_000, 0, [])));
            repo.Commit("first");
            repo.Baseline("tokenledger", "chat", Calls(("gpt-4.1-2025-04-14", 2_000_000, 0, [])));
            repo.File("tokenledger/pricing.json", """{ "currency": "EUR", "models": { "gpt-4.1": { "input": 2, "output": 8 } } }""");

            var (_, text) = repo.Tool("diff");

            Assert.Contains("estimated cost 2.00 EUR → 4.00 EUR per run", text);
            Assert.Contains("All scenarios, estimated cost per run: 2.00 EUR → 4.00 EUR (+100 %)", text);
        });

        Test("diff names models without a price", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "chat", Calls(("mystery-model", 10, 1, [])));
            repo.Commit("first");
            repo.Baseline("tokenledger", "chat", Calls(("mystery-model", 100, 1, [])));

            Assert.Contains("No price for mystery-model: costs leave them out.", repo.Tool("diff").Output);
        });

        Test("diff in Markdown has a table, details per scenario and a summary line", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "support/chat", Calls(("m", 1000, 100, [])));
            repo.Commit("first");
            repo.Baseline("tokenledger", "support/chat", Calls(("m", 1000, 100, []), ("m", 1000, 100, [])));

            var (_, md) = repo.Tool("diff", "--format", "markdown");

            Assert.Contains("### TokenLedger", md);
            Assert.Contains("**1 regression** · 0 changes · 0 improvements · 0 new · 0 removed · 0 unchanged · `HEAD` → `working tree`", md);
            Assert.Contains("| `support/chat` | 1 → 2 | 1,100 → 2,200 | no price | 🔴 regression |", md);
            Assert.Contains("<details><summary><code>support/chat</code></summary>", md);
            Assert.Contains("- 🔴 **regression**: model calls 1 → 2", md);
        });

        Test("diff in JSON carries the counts, each scenario and the cost", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "a", Calls(("m", 10, 1, [])));
            repo.Commit("first");
            repo.Baseline("tokenledger", "a", Calls(("m", 10, 1, []), ("m", 10, 1, ["t"])));
            repo.Baseline("tokenledger", "b", Calls(("m", 10, 1, [])));
            var output = Path.Combine(repo.Root, "report.json");

            Assert.Equal(Cli.Ok, repo.Tool("diff", "--format", "json", "--output", output).Code);

            using var json = JsonDocument.Parse(File.ReadAllText(output));
            var summary = json.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("regression").GetInt32());
            Assert.Equal(1, summary.GetProperty("added").GetInt32());
            Assert.Equal(3, summary.GetProperty("regressions").GetInt32());   // calls, input and output tokens
            var a = json.RootElement.GetProperty("scenarios")[0];
            Assert.Equal("regression", a.GetProperty("status").GetString());
            Assert.Equal(2, a.GetProperty("after").GetProperty("calls").GetInt32());
            Assert.Equal("t", a.GetProperty("after").GetProperty("tools")[0].GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cost").GetProperty("after").ValueKind);
        });

        Test("With several test projects, scenarios are prefixed with their project", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tests/A.Tests/tokenledger", "chat", Calls(("m", 10, 1, [])));
            repo.Baseline("tests/B.Tests/tokenledger", "chat", Calls(("m", 10, 1, [])));
            repo.Commit("first");
            repo.Baseline("tests/B.Tests/tokenledger", "chat", Calls(("m", 10, 1, []), ("m", 10, 1, [])));

            var (_, text) = repo.Tool("diff");

            Assert.Contains("tests/B.Tests: chat  regression", text);
            Assert.DoesNotContain("A.Tests", text);
        });

        Test("--tolerance and --dir change what diff compares", () =>
        {
            using var repo = new Repo();
            repo.Baseline("ai-ledger", "chat", Calls(("m", 100, 10, [])));
            repo.Commit("first");
            repo.Baseline("ai-ledger", "chat", Calls(("m", 120, 10, [])));

            Assert.Contains("No baselines found.", repo.Tool("diff").Output);
            Assert.Contains("input tokens 100 → 120", repo.Tool("diff", "--dir", "ai-ledger").Output);
            Assert.Contains("No changes in 1 scenario.", repo.Tool("diff", "--dir", "ai-ledger", "--tolerance", "0.25").Output);
        });

        Test("summary shows what each scenario costs", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "a", Calls(("m", 1_000_000, 0, [])));
            repo.Baseline("tokenledger", "b", Calls(("m", 500_000, 0, []), ("m", 500_000, 0, [])));
            repo.File("tokenledger/pricing.json", """{ "models": { "m": { "input": 1, "output": 1 } } }""");

            var (_, text) = repo.Tool("summary");

            Assert.Contains("Scenario  ", text);
            Assert.Contains("1,000,000          1.00 USD", text);
            Assert.Contains("2 scenarios, 3 model calls, 2,000,000 tokens, 2.00 USD per run of the suite.", text);
            Assert.Contains("| `b` | 2 | 1,000,000 | 1.00 USD |", repo.Tool("summary", "--format", "markdown").Output);
        });

        Test("Usage and git errors exit with code 2 and say what to do", () =>
        {
            using var repo = new Repo();
            repo.Commit("first");

            var (code, error) = repo.Tool("diff", "no-such-ref");
            Assert.Equal(Cli.UsageError, code);
            Assert.Contains("'no-such-ref' is not a git ref here", error);

            Assert.Equal(Cli.UsageError, repo.Tool("diff", "--format", "xml").Code);
            Assert.Equal(Cli.UsageError, repo.Tool("diff", "--tolerance", "2").Code);
            Assert.Equal(Cli.UsageError, repo.Tool("frobnicate").Code);
            Assert.Equal(Cli.UsageError, repo.Tool().Code);
            Assert.Equal(Cli.Ok, repo.Tool("--help").Code);
        });

        Test("pricing init creates pricing.json with the models used, without snapshot dates", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tests/A.Tests/tokenledger", "chat", Calls(("gpt-4.1-2025-04-14", 1, 1, []), ("claude-sonnet-4-5-20250929", 1, 1, [])));
            repo.File("tests/A.Tests/tokenledger/recordings/chat.json", """{ "interactions": [ { "response": { "model": "gemini-2.5-pro" } } ] }""");

            var (code, text) = repo.Tool("pricing", "init", "--currency", "eur");

            Assert.Equal(Cli.Ok, code);
            Assert.Contains("tests/A.Tests/tokenledger/pricing.json: created with claude-sonnet-4-5, gemini-2.5-pro, gpt-4.1.", text);
            var pricing = File.ReadAllText(Path.Combine(repo.Root, "tests/A.Tests/tokenledger/pricing.json"));
            Assert.Contains("// Prices per million tokens", pricing);
            Assert.Contains("\"currency\": \"EUR\"", pricing);
            Assert.Contains("\"gpt-4.1\": {\n      \"input\": null,\n      \"output\": null\n    }", pricing);
            Assert.Equal(0, LedgerPricing.Load(Path.Combine(repo.Root, "tests/A.Tests/tokenledger/pricing.json")).Models.Count);
        });

        Test("pricing init adds only new models and keeps the prices already there", () =>
        {
            using var repo = new Repo();
            repo.Baseline("tokenledger", "chat", Calls(("gpt-4.1-2025-04-14", 1, 1, []), ("o4-mini", 1, 1, [])));
            repo.File("tokenledger/pricing.json", """{ "currency": "EUR", "models": { "gpt-4.1": { "input": 1.85, "output": 7.40 } } }""");

            Assert.Contains("tokenledger/pricing.json: added o4-mini.", repo.Tool("pricing", "init").Output);
            var pricing = LedgerPricing.Load(Path.Combine(repo.Root, "tokenledger/pricing.json"));
            Assert.Equal(1.85m, pricing.Find("gpt-4.1-2025-04-14")?.Input);
            Assert.Equal("EUR", pricing.Currency);

            Assert.Contains("every model already has an entry (2 model(s))", repo.Tool("pricing", "init").Output);
        });

        Test("pricing init without any TokenLedger folder explains why", () =>
        {
            using var repo = new Repo();
            var (code, error) = repo.Tool("pricing", "init");
            Assert.Equal(Cli.UsageError, code);
            Assert.Contains("No 'tokenledger' folder with baselines or recordings", error);
        });
    }

    private static List<LedgerCall> Calls(params (string Model, long Input, long Output, string[] Tools)[] calls) =>
        [.. calls.Select((c, i) => new LedgerCall { Index = i + 1, Model = c.Model, InputTokens = c.Input, OutputTokens = c.Output, Tools = c.Tools })];

    /// <summary>A temporary git repository.</summary>
    private sealed class Repo : IDisposable
    {
        public Repo()
        {
            Root = Path.Combine(Path.GetTempPath(), "tokenledger-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Git.Run(Root, "init", "-q", "-b", "main");
            Git.Run(Root, "config", "user.email", "tests@tokenledger.invalid");
            Git.Run(Root, "config", "user.name", "TokenLedger tests");
            Git.Run(Root, "config", "commit.gpgsign", "false");
        }

        public string Root { get; }

        public void Baseline(string folder, string scenario, IReadOnlyList<LedgerCall> calls) =>
            File($"{folder}/baselines/{scenario}.json", Baselines.Serialize(Baselines.Build(scenario, calls)));

        public void File(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
        }

        public void Commit(string tag)
        {
            Git.Run(Root, "add", "-A");
            Git.Run(Root, "commit", "-q", "--allow-empty", "-m", tag);
            Git.Run(Root, "tag", tag);
        }

        public (int Code, string Output) Tool(params string[] args)
        {
            var output = new StringWriter();
            var code = Cli.Run(args, output, output, Root);
            return (code, output.ToString());
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

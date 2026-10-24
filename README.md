<img src="assets/icon.png" alt="" width="96" align="right">

# TokenLedger

**AI cost and behavior snapshots for .NET apps that use `IChatClient`.**

A prompt tweak, a new tool or a refactor can double what a flow costs, or send an agent into a loop, and nobody
notices until the invoice arrives. TokenLedger turns the AI behavior of your app into something you review in each
pull request: how many model calls each flow makes, how many tokens it uses, which tools it calls and what it costs,
compared with the previous version.

- **Record once, replay for free.** The first run calls the real model and saves its responses; CI replays them with
  no network and no cost, so tests are deterministic.
- **Measure each scenario.** Model calls, input, output, cached and reasoning tokens, models and tool calls, in order.
- **Baselines in the repository.** One small JSON file per scenario. A run that differs fails with what changed:
  *regression*, *change* or *improvement*.
- **Budgets.** Maximum calls, tokens or cost per scenario.
- **Your prices.** TokenLedger ships no price table: you put your own rates in `pricing.json`, so discounts, regions
  and currencies are right, and a price change never shows up as a code regression.
- **Any provider.** OpenAI, Azure OpenAI, Anthropic, Ollama: anything behind `Microsoft.Extensions.AI`, streaming included.

> Status: **0.2 preview**. See the [roadmap](#roadmap).

## Install

```bash
dotnet add package TokenLedger --prerelease
```

## Use

**1. Add the middleware to your chat client**, last in the pipeline so it sees each model round trip.
Outside a test scenario it passes every call through untouched, so it can stay in production code.

```csharp
builder.Services.AddChatClient(sp => new OpenAIClient(key).GetChatClient("gpt-4.1").AsIChatClient())
    .UseFunctionInvocation()
    .UseTokenLedger();
```

**2. Open a scenario in a test.** Any test framework works: a failure is an exception.

```csharp
[Fact]
public async Task Support_chat_answers_an_order_question()
{
    await using var ledger = Ledger.Scenario("support-chat/order-status");

    var answer = await chat.GetResponseAsync("Where is my order 42?");

    Assert.Contains("42", answer.Text);
    ledger.Budget(maxCalls: 3, maxTokens: 4_000);   // optional
}   // disposing the scenario compares the calls with its baseline
```

**3. Run the tests and commit the `tokenledger` folder.** The first run calls the real model and writes:

```text
tokenledger/
  recordings/support-chat/order-status.json   the model's responses, replayed from now on
  baselines/support-chat/order-status.json    what the scenario costs and does
```

```json
{
  "format": 1,
  "scenario": "support-chat/order-status",
  "calls": [
    {"model":"gpt-4.1-2025-04-14","input":1180,"output":96,"tools":["search_orders"]},
    {"model":"gpt-4.1-2025-04-14","input":1402,"output":141,"cachedInput":1024,"tools":[]}
  ],
  "totals": {"calls":2,"input":2582,"output":237,"cachedInput":1024,"reasoning":0}
}
```

When a later change makes the flow cost more, the test fails:

```text
TokenLedger: scenario 'support-chat/order-status' failed.
The calls differ from the baseline tokenledger/baselines/support-chat/order-status.json:
  regression  model calls 2 → 5
  regression  input tokens 2,582 → 9,940 (+285 %)
  change      tools: search_orders → search_orders, get_customer, search_orders
  estimated cost 0.005107 EUR → 0.0213 EUR per run
If the change is intended, run the tests with TOKENLEDGER_UPDATE=1 and commit the baseline.
```

## In pull requests

`dotnet tokenledger diff` compares the baselines of two git refs, and a GitHub Action keeps the report as a comment on
each pull request:

```yaml
- uses: amondejar/Token-Ledger/actions/diff@v0.2.0-preview.1
  with:
    fail-on: regression
```

> ### TokenLedger
>
> **1 regression** · 0 changes · 0 improvements · 0 new · 0 removed · 3 unchanged · `main` → `HEAD`
>
> | Scenario | Model calls | Tokens | Cost per run | |
> | --- | ---: | ---: | ---: | --- |
> | `support-chat/order-status` | 2 → 4 | 2,819 → 8,344 | 0.005107 EUR → 0.018 EUR | 🔴 regression |

On your machine:

```bash
dotnet tool install --global TokenLedger.Tool --prerelease
dotnet tokenledger diff                          # what your last test run changed
dotnet tokenledger summary                       # what each scenario costs
dotnet tokenledger pricing init --currency EUR   # pricing.json with an entry per model you use
```

Details in [Comparing branches and commenting on pull requests](docs/diff.md).

## Modes

| Mode | What it does | Default |
| --- | --- | --- |
| `auto` | Replays what is recorded, calls the real model for the rest and records it | On your machine |
| `replay` | Replays only; a request without a recording fails | In CI |
| `record` | Calls the real model for every request and rewrites the recordings | |
| `live` | Calls the real model and measures it, without recordings | |

Set it with `TOKENLEDGER_MODE=record` (it wins) or `Ledger.Options.Mode`. `TOKENLEDGER_UPDATE=1` accepts the
current measurements and rewrites the baselines. CI is detected from the usual variables (`CI`, `GITHUB_ACTIONS`,
`TF_BUILD`, ...); there, a scenario without a baseline fails instead of creating one.

## Prices

Put your rates per million tokens in `tokenledger/pricing.json`:

```json
// Prices per million tokens, from our contract.
{
  "currency": "EUR",
  "models": {
    "gpt-4.1":           { "input": 1.85, "output": 7.40, "cachedInput": 0.46 },
    "claude-sonnet-4-5": { "input": 2.75, "output": 13.80 }
  }
}
```

or in code: `Ledger.Options.Pricing.Add("gpt-4.1", input: 1.85m, output: 7.40m, cachedInput: 0.46m)`.

`gpt-4.1` also prices dated snapshots such as `gpt-4.1-2025-04-14`, but not `gpt-4.1-mini`; a key ending in `*`
matches any model that starts with it. Baselines store tokens, never money: the cost is computed with the prices of
the day, so a provider's price change never looks like a regression. Without prices everything still works, in calls
and tokens.

## Configuration

Set it once, for example in a module initializer of the test project:

```csharp
internal static class TokenLedgerSetup
{
    [ModuleInitializer]
    public static void Configure() => Ledger.Configure(options =>
    {
        options.NormalizeTimestamps().NormalizeGuids();        // variable data must not prevent a replay
        options.Redact(new Regex(@"\b\d{16}\b"), "<card>");    // keep secrets out of the recordings
        options.Tolerance = 0.10;                              // ignore token changes up to 10 %
        options.FailOn = LedgerFailOn.Regression;              // default: Change (any difference)
        options.DefaultBudget = new LedgerBudget { MaxCalls = 10 };
    });
}
```

A complete example is in [samples/SupportBot.Tests](samples/SupportBot.Tests): an agent with a tool, two scenarios, and
their committed recordings and baselines.

More in the [guide](docs/guide.md): how requests are matched, ASP.NET Core integration tests, parallel calls,
and what to commit.

## What it is not

- **Production observability.** `Microsoft.Extensions.AI` already emits OpenTelemetry token metrics (`UseOpenTelemetry()`).
- **Answer quality.** Relevance, groundedness and safety are what `Microsoft.Extensions.AI.Evaluation` and skUnit
  do; TokenLedger runs alongside them.
- **A tokenizer.** It reports the tokens the provider counted; it never estimates them.

## Roadmap

1. **0.1** ✅: middleware, record and replay, measurement per scenario, baselines with `TOKENLEDGER_UPDATE`, budgets, your prices.
2. **0.2** ✅: `dotnet tokenledger diff` (text, Markdown, JSON), `summary`, `pricing init`, and a GitHub Action that comments on the pull request.
3. **0.3**: cost per ASP.NET Core endpoint in `WebApplicationFactory` tests.
4. **0.4**: a scheduled workflow that re-records against the real model and opens a pull request with what the provider changed.
5. **1.0**: public API frozen for two previews, full documentation and samples with OpenAI, Azure OpenAI and Ollama.

## License

[MIT](LICENSE).

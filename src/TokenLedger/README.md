# TokenLedger

AI cost and behavior snapshots for .NET apps that use `IChatClient`.

Record the model's responses once and replay them in CI for free; measure calls, tokens and tool calls per test
scenario; compare them with a baseline committed to the repository and fail on regressions or budgets. Prices are
yours to configure. Works with any `Microsoft.Extensions.AI` provider, streaming included.

```csharp
// The chat client: TokenLedger last, closest to the provider. Outside a test scenario it does nothing.
services.AddChatClient(inner).UseFunctionInvocation().UseTokenLedger();

// A test, in any framework:
await using var ledger = Ledger.Scenario("support-chat/order-status");
var answer = await chat.GetResponseAsync("Where is my order 42?");
ledger.Budget(maxCalls: 3, maxTokens: 4_000);
// Disposing the scenario compares the calls with tokenledger/baselines/support-chat/order-status.json.
```

`TOKENLEDGER_MODE` = `auto` (default on your machine), `replay` (default in CI), `record` or `live`.
`TOKENLEDGER_UPDATE=1` accepts the current measurements.

Documentation: https://github.com/amondejar/Token-Ledger

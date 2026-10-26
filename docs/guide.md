# TokenLedger guide

## How a call goes through TokenLedger

1. Your code calls `IChatClient` as usual.
2. `TokenLedgerChatClient` looks for the scenario open in the current async flow (`Ledger.Current`). With none, it
   passes the call through untouched and that is all.
3. Inside a scenario it computes the request's **fingerprint** and, in `auto` or `replay`, looks for a recorded
   response with the same fingerprint. In `record`, or in `auto` without a recording, it calls the real model and
   keeps the response for the recording.
4. It notes the model, the token usage the provider reported and the tools the model asked for.
5. Disposing the scenario saves the recording, checks the budgets and compares the calls with the baseline.

## Where to put the middleware

Last, closest to the provider:

```csharp
services.AddChatClient(inner)
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .UseTokenLedger();
```

Each step of `ChatClientBuilder` wraps the ones after it, so the last one talks to the provider. There TokenLedger
sees every round trip of a tool loop as its own call, with its own tokens, and your tool functions still run for real
during a replay: only the model is replaced. Put first, it would see the whole tool loop as one call.

You can also wrap a client by hand: `new TokenLedgerChatClient(inner)`.

## How requests are matched

The fingerprint is a hash of what changes the model's answer:

- the model id (`ChatOptions.ModelId`, or the client's default model);
- `Instructions`, temperature, top-p, top-k, penalties, seed, maximum output tokens, stop sequences;
- the response format (and its JSON schema), the tool mode, and the tools' names and descriptions;
- every message: role, author, text, tool calls (name and arguments) and tool results.

Left out on purpose: tool call ids, which providers generate and differ on each run, and reasoning text.

When a request carries data that changes on every run (today's date, a new GUID, a random order number), the
fingerprint changes too and the replay misses. Normalize that data before matching:

```csharp
Ledger.Options
    .NormalizeTimestamps()                                         // 2026-10-24, 2026-10-24T07:47:00Z
    .NormalizeGuids()
    .Normalize(new Regex(@"ORD-\d+"), "ORD-<n>")
    .Normalize(text => text.Replace(Environment.MachineName, "<machine>"));
```

Normalizers apply to matching only: the model still receives the real request.

The same request repeated in a scenario gets the recorded answers in order; once they run out, the last one again.

A miss in `replay` throws `TokenLedgerException` from the call itself, with the model, the number of messages and
the last message, and is listed again when the scenario closes.

## ASP.NET Core integration tests

`TokenLedger.AspNetCore` measures the model calls an HTTP request makes and attributes each one to its endpoint.

```bash
dotnet add package TokenLedger.AspNetCore --prerelease
```

Add its middleware to the application under test, then send the test's requests through `LedgerHttpHandler`:

```csharp
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddTokenLedger());
}

[Fact]
public async Task Support_chat_answers()
{
    await using var ledger = Ledger.Scenario("api/support-chat");
    var client = factory.CreateDefaultClient(new LedgerHttpHandler(ledger));

    var response = await client.PostAsJsonAsync("/api/support/chat", new { message = "Where is my order 42?" });

    response.EnsureSuccessStatusCode();
    ledger.Budget(maxCalls: 3);
}
```

- **The handler** adds a `TokenLedger-Scenario` header to each request, and the middleware joins the request to that
  open scenario. That works whether or not the request runs in the test's async flow: `WebApplicationFactory`,
  `TestServer`, or the application on a real port (`new HttpClient(new LedgerHttpHandler(ledger, new SocketsHttpHandler()))`).
- **Each call records its endpoint** as the method and the route pattern: `GET orders/{id}`, never the path with its
  ids. `LedgerCall.Endpoint` has it, and the baseline gains an `endpoints` section:

```json
  "endpoints": {
    "POST api/support/chat": {"calls":2,"input":182,"output":27}
  }
```

- **A change per endpoint is reported by name**, in the test and in `dotnet tokenledger diff`:
  `regression  POST api/support/chat: model calls 2 → 3`.
- **In production** the middleware finds no open scenario and does nothing, but it belongs to the test host:
  register it in `ConfigureWebHost` (or the hook your test fixture has), not in `Program.cs`.

Without the package, a request that runs in the test's async flow is still measured, without its endpoint:
`TestServer` keeps that flow with `builder.UseTestServer(o => o.PreserveExecutionContext = true)`.

## Parallel calls

Calls are measured in the order they finish. When a flow calls the model in parallel (`Task.WhenAll`), that order can
change between runs; replay still works (it matches by fingerprint), but the tool sequence in the baseline may flip.
For those scenarios use `ledger.FailOn = LedgerFailOn.Regression`, which ignores behavior changes and still fails on
more calls or tokens.

Tests themselves can run in parallel: each scenario is separate, as long as each has its own name.

## Streaming

Streaming calls are recorded when the stream is read to the end, and measured from the `UsageContent` the provider
sends. A recording made from a stream replays as a stream or as a normal response, and the other way round. Some
providers report no usage when streaming unless asked to: those calls show as "without usage data" instead of zero.

## Baselines and failures

A baseline is compared on the number of calls, total input and output tokens (with the tolerance, 10 % by default),
the models used and the sequence of tools. `FailOn` decides what fails:

| `FailOn` | Fails on |
| --- | --- |
| `Change` (default) | any difference: regressions, behavior changes and improvements, so the baseline stays current |
| `Regression` | more calls, or more tokens beyond the tolerance |
| `Never` | nothing; `ledger.Differences` still lists them. Budgets still apply |

To accept a difference, run the tests with `TOKENLEDGER_UPDATE=1` and commit the baselines.

`Verify()` runs when the scenario is disposed. If the test failed for another reason first, the scenario still
verifies on the way out and can add its own failure; read the first one. To verify at a point of your choosing, call
`ledger.Verify()` yourself; disposing afterwards does nothing.

When a call misses its recording, the scenario neither compares nor rewrites the baseline: the numbers are incomplete.

## What to commit

Commit the whole `tokenledger` folder: recordings, baselines and `pricing.json`. Recordings hold the model's answers
and a one-line summary of each request; redact anything that must not be in the repository:

```csharp
Ledger.Options.Redact(new Regex(@"\b[\w.+-]+@[\w-]+\.[\w.]+\b"), "<email>");
```

Redaction applies to what is written; replays return the redacted text.

## When the provider changes

Replays do not notice when the provider changes the model behind a name. Re-record from time to time
(`TOKENLEDGER_MODE=record`) and review the diff. 0.4 adds a scheduled workflow that does this and opens a pull request.

## Limits in 0.1

- Text, reasoning, tool calls and tool results are recorded. Images, audio and other content in responses are not.
- The folder is found from the test project's source path, or from the test assembly when the build maps paths
  (deterministic CI builds). Set `Ledger.Options.Directory` or `TOKENLEDGER_DIR` to put it elsewhere.

## Settings

| Setting | Code | Environment |
| --- | --- | --- |
| Mode | `Ledger.Options.Mode` | `TOKENLEDGER_MODE` (wins) |
| Accept the current measurements | `Ledger.Options.UpdateBaselines` | `TOKENLEDGER_UPDATE=1` |
| Folder | `Ledger.Options.Directory` | `TOKENLEDGER_DIR` (wins) |
| What fails | `Ledger.Options.FailOn`, `ledger.FailOn` | |
| Token tolerance | `Ledger.Options.Tolerance`, `ledger.Tolerance` | |
| Prices | `Ledger.Options.Pricing`, `tokenledger/pricing.json` | |
| Budget for every scenario | `Ledger.Options.DefaultBudget` | |

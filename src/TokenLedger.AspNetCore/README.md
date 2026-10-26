# TokenLedger.AspNetCore

[TokenLedger](https://github.com/amondejar/Token-Ledger) for ASP.NET Core integration tests: each model call is
attributed to the HTTP endpoint that made it, so baselines and pull request reports show the cost per endpoint.

```csharp
// The test host: add TokenLedger's middleware.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddTokenLedger());
}

// A test: requests sent through LedgerHttpHandler are measured in the scenario.
await using var ledger = Ledger.Scenario("api/support-chat");
var client = factory.CreateDefaultClient(new LedgerHttpHandler(ledger));
var response = await client.PostAsJsonAsync("/api/support/chat", new { message = "Where is my order 42?" });
```

The baseline gains an `endpoints` section, and a change in the calls of one endpoint is reported by name:

```text
regression  POST api/support/chat: model calls 2 → 3
```

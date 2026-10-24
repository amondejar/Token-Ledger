using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

internal static class PricingTests
{
    public static void Register()
    {
        Test("A price matches its model and dated snapshots, not other models with the same prefix", () =>
        {
            var pricing = new LedgerPricing()
                .Add("gpt-4.1", input: 2m, output: 8m)
                .Add("gpt-4.1-mini", input: 0.4m, output: 1.6m)
                .Add("claude-sonnet-4-5", input: 3m, output: 15m)
                .Add("my-deployment-*", input: 1m, output: 1m);

            Assert.Equal(2m, pricing.Find("gpt-4.1")?.Input);
            Assert.Equal(2m, pricing.Find("GPT-4.1-2025-04-14")?.Input);
            Assert.Equal(0.4m, pricing.Find("gpt-4.1-mini-2025-04-14")?.Input);
            Assert.Equal(3m, pricing.Find("claude-sonnet-4-5-20250929")?.Input);
            Assert.Equal(1m, pricing.Find("my-deployment-eu")?.Input);
            Assert.True(pricing.Find("gpt-4.1-nano") is null);
            Assert.True(pricing.Find("gpt-4") is null);
        });

        Test("Cached input tokens are charged at their own price, as part of the input", () =>
        {
            var pricing = new LedgerPricing().Add("m", input: 2m, output: 8m, cachedInput: 0.5m);
            // 1,000 input of which 400 cached, 100 output: 600 × 2 + 400 × 0.5 + 100 × 8 = 2,200 per million.
            Assert.Equal(0.0022m, pricing.Cost("m", 1_000, 100, cachedInputTokens: 400));
            Assert.Equal(0.0028m, new LedgerPricing().Add("m", 2m, 8m).Cost("m", 1_000, 100, 400));
            Assert.True(pricing.Cost("other", 1, 1) is null);
        });

        Test("pricing.json is read with comments, and entries without prices are skipped", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"tokenledger-pricing-{Guid.NewGuid():N}.json");
            File.WriteAllText(file, """
                // Prices per million tokens, from our contract.
                {
                  "currency": "EUR",
                  "models": {
                    "gpt-4.1": { "input": 1.85, "output": 7.40, "cachedInput": 0.46 },
                    "to-fill-in": { "input": null, "output": null },
                  }
                }
                """);
            try
            {
                var pricing = LedgerPricing.Load(file);
                Assert.Equal("EUR", pricing.Currency);
                Assert.Equal(1, pricing.Models.Count);
                Assert.Equal(new ModelPrice(1.85m, 7.40m, 0.46m), pricing.Models["gpt-4.1"]);
            }
            finally
            {
                File.Delete(file);
            }
        });

        Test("A scenario uses pricing.json from the TokenLedger folder, with code entries on top", () =>
        {
            using var sandbox = new Sandbox(LedgerMode.Live);
            Directory.CreateDirectory(sandbox.Directory);
            File.WriteAllText(Path.Combine(sandbox.Directory, "pricing.json"),
                """{ "currency": "EUR", "models": { "a": { "input": 1, "output": 1 }, "b": { "input": 1, "output": 1 } } }""");
            Ledger.Options.Pricing.Add("b", input: 5m, output: 5m);

            using var scenario = Ledger.Scenario("pricing/merge");

            Assert.Equal(1m, scenario.Pricing.Find("a")?.Input);
            Assert.Equal(5m, scenario.Pricing.Find("b")?.Input);
            Assert.Equal("USD", scenario.Pricing.Currency);   // code entries bring the code's currency
        });

        Test("An invalid pricing file and negative prices are rejected", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"tokenledger-pricing-{Guid.NewGuid():N}.json");
            File.WriteAllText(file, "{ \"models\": [] }");
            try
            {
                Assert.Contains("is not a valid pricing file", Assert.Throws<TokenLedgerException>(() => LedgerPricing.Load(file)).Message);
            }
            finally
            {
                File.Delete(file);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerPricing().Add("m", -1m, 1m));
        });
    }
}

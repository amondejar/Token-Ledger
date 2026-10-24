using TokenLedger.Tests;

Console.WriteLine("TokenLedger tests\n");
ScenarioTests.Register();
BaselineTests.Register();
PricingTests.Register();
PublicApiTests.Register();
return await Harness.RunAsync();

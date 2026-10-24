using TokenLedger.Tests;

Console.WriteLine("TokenLedger tests\n");
ScenarioTests.Register();
BaselineTests.Register();
PricingTests.Register();
ToolTests.Register();
PublicApiTests.Register();
return await Harness.RunAsync();

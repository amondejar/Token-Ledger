# Contributing

Thanks for helping. Issues and pull requests are welcome; for a large change, open an issue first so we can agree on
the approach.

## Build and test

You need the .NET 10 SDK.

```bash
dotnet build
dotnet run --project tests/TokenLedger.Tests
```

The tests are a small dependency-free runner (`dotnet run`), so they run anywhere the SDK does. Warnings are errors.

## Public API

`src/TokenLedger/PublicAPI.txt` lists the public API, one member per line. A change to the API fails the tests until
you update the file with `TOKENLEDGER_UPDATE_PUBLIC_API=1 dotnet run --project tests/TokenLedger.Tests` and commit it,
so every API change shows up in the pull request. Explain why in the pull request.

## Pull requests

- Tests for the change.
- A line in `CHANGELOG.md` under `[Unreleased]`.
- One topic per pull request.

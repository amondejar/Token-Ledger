# Changelog

All notable changes to TokenLedger are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0-preview.1]

### Added

- `TokenLedger.Tool`, the `dotnet tokenledger` command:
  - `diff [<base> [<head>]]` compares the baselines of two git refs (default `HEAD` and the working tree) and lists
    each scenario as regression, change, improvement, new, removed or unchanged, with the estimated cost per run and
    for the whole suite. Text, Markdown or JSON; `--fail-on regression|change` for CI.
  - `summary [<ref>]`: what each scenario costs.
  - `pricing init`: creates or completes `pricing.json` with an empty entry for each model the scenarios use.
- `actions/diff`: a GitHub Action that runs the diff on pull requests, keeps one comment up to date, writes the job
  summary and can fail the check on regressions.
- `samples/SupportBot.Tests`: an agent with a tool and two scenarios, with their recordings and baselines.

## [0.1.0-preview.1]

First preview.

### Added

- `TokenLedgerChatClient` and `UseTokenLedger()`: an `IChatClient` middleware that does nothing outside a test
  scenario, so it can stay in production code.
- `Ledger.Scenario(name)`: replays or records the model calls of a test flow and measures them: model, input,
  output, cached and reasoning tokens, and tool calls, streaming included.
- Modes `auto`, `replay`, `record` and `live`, from `TOKENLEDGER_MODE` or `Ledger.Options.Mode`; `replay` is the
  default in CI.
- Recordings matched by a fingerprint of the request, with normalizers for variable data (`NormalizeTimestamps()`,
  `NormalizeGuids()`, `Normalize(...)`) and redactors to keep secrets out of the repository (`Redact(...)`).
- Baselines per scenario, one call per line, compared on calls, tokens (with a tolerance), models and tool sequence;
  differences classified as regression, change or improvement. `FailOn` chooses what fails; `TOKENLEDGER_UPDATE=1`
  accepts the changes.
- Budgets per scenario or for all of them: maximum calls, input, output or total tokens, and cost.
- Your prices per million tokens, in `tokenledger/pricing.json` or in code, matched to dated model snapshots; no
  prices ship with the package.

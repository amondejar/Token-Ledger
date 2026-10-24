# Changelog

All notable changes to TokenLedger are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

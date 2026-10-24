# TokenLedger.Tool

`dotnet tokenledger`: compares the [TokenLedger](https://github.com/amondejar/Token-Ledger) baselines of two git
refs and lists, per scenario, the regressions, behavior changes and improvements in model calls, tokens, tools and
estimated cost.

```bash
dotnet tool install --global TokenLedger.Tool --prerelease

dotnet tokenledger diff                          # what your last test run changed (HEAD → working tree)
dotnet tokenledger diff origin/main HEAD --fail-on regression
dotnet tokenledger diff v0.3.0 --format markdown --output report.md
dotnet tokenledger summary                       # what each scenario costs today
dotnet tokenledger pricing init --currency EUR   # pricing.json with an entry per model you use
```

```text
TokenLedger: HEAD → working tree
1 regression, 0 changes, 0 improvements, 0 new, 0 removed, 0 unchanged

support/chat  regression
  regression  model calls 2 → 4
  regression  input tokens 2,582 → 7,886 (+205 %)
  change      tools: search_orders → search_orders, get_customer, search_orders
  estimated cost 0.005107 EUR → 0.018 EUR per run
```

Reports as text, Markdown (for pull request comments) or JSON. Exit code 1 with `--fail-on regression` or
`--fail-on change` when there is one; 2 on usage errors. In GitHub Actions, the
[`amondejar/Token-Ledger/actions/diff`](https://github.com/amondejar/Token-Ledger/tree/main/actions/diff) action runs
it and keeps a pull request comment up to date.

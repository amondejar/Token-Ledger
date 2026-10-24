# Comparing branches and commenting on pull requests

The baselines are plain files in the repository, so a pull request already shows them in its diff. `dotnet tokenledger`
turns that diff into a report a reviewer reads in seconds, and a GitHub Action keeps it as a comment on the pull request.

## The tool

```bash
dotnet tool install --global TokenLedger.Tool --prerelease
```

or, to pin it per repository, as a local tool:

```bash
dotnet new tool-manifest
dotnet tool install TokenLedger.Tool --prerelease
dotnet tokenledger --help
```

### diff

```bash
dotnet tokenledger diff [<base> [<head>]]
```

`<base>` and `<head>` are git refs (a branch, a tag, a commit). `<base>` defaults to `HEAD` and `<head>` to the working
tree, new files included, so a bare `dotnet tokenledger diff` after `TOKENLEDGER_UPDATE=1` shows what you are about to
commit.

It finds every `tokenledger/baselines/**.json` in the repository on both sides, so several test projects work, and
compares each scenario the same way the tests do: model calls, input and output tokens (with `--tolerance`, 10 % by
default), models and the sequence of tools. Each scenario is a **regression**, a **change**, an **improvement**,
**new**, **removed** or **unchanged**; the worst difference decides.

Costs use the prices of the day: `pricing.json` from `<head>` (else from `<base>`) for both sides, or `--pricing <file>`.
So the report reflects what the code changed, never what the provider's price list did.

| Option | |
| --- | --- |
| `--format text\|markdown\|json` | Text by default. Markdown is what the action posts. JSON for your own scripts. |
| `--output <file>` | Write the report to a file. |
| `--fail-on regression\|change\|never` | Exit with code 1 when a scenario regressed, or when anything changed. Default `never`. |
| `--tolerance <0..1>` | Relative token change to ignore. |
| `--pricing <file>` | Prices for every folder instead of their `pricing.json`. |
| `--dir <name>` | The TokenLedger folder name, if you changed it with `Ledger.Options.Directory`. |

Exit codes: 0 fine, 1 changes at or above `--fail-on`, 2 a usage or git error.

### summary

```bash
dotnet tokenledger summary [<ref>] [--format markdown]
```

What each scenario costs at a ref (default the working tree), and the suite in total.

### pricing init

```bash
dotnet tokenledger pricing init [<folder>] [--currency EUR]
```

Creates `pricing.json` in every TokenLedger folder below the current one (or in `<folder>`), with an empty entry for
each model in the baselines and recordings, without dated snapshot suffixes (`gpt-4.1-2025-04-14` becomes `gpt-4.1`).
Run it again after adding scenarios: it adds the new models and never touches the prices already there.

## The GitHub Action

```yaml
name: TokenLedger

on: pull_request

jobs:
  diff:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write   # to comment
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - uses: amondejar/Token-Ledger/actions/diff@v0.2.0-preview.1
        with:
          fail-on: regression
```

It compares the pull request's base with its head, writes the Markdown report to the job summary, posts it as a
comment and updates that same comment on every push. With `fail-on`, the check fails on regressions (or any change).

The action does not run your tests: the baselines in the pull request are what the author's test run produced. Keep
your normal test job, which replays the recordings and fails when the committed baselines do not match the code.

| Input | Default | |
| --- | --- | --- |
| `fail-on` | `never` | `regression`, `change` or `never`. |
| `base` | the pull request's base commit | Any git ref. |
| `tolerance` | `0.10` | Relative token change to ignore. |
| `dir` | `tokenledger` | The TokenLedger folder name. |
| `comment` | `true` | Post or update the pull request comment. |
| `version` | latest, previews included | `TokenLedger.Tool` version. |

Outputs: `regressions` (scenarios with a regression), `changed` and `report` (path of the Markdown file).

Pull requests from forks get a read-only token: the action then leaves the report in the job summary and warns that it
could not comment.

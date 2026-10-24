# Security policy

## Reporting a vulnerability

Please do not open a public issue. Report it privately through GitHub:
**Security** tab of this repository, **Report a vulnerability**. You will get an answer within a week.

Include the package version, what an attacker can do, and the smallest app or test that shows it. A fix is
released as a patch version and credited to you in the changelog, unless you prefer otherwise.

## Supported versions

| Version | Supported |
| --- | --- |
| Latest 0.x preview | Yes |

TokenLedger runs on Microsoft.Extensions.AI; vulnerabilities in it go to
[Microsoft](https://github.com/dotnet/extensions/security/policy).

## Recordings

Recordings hold real model responses and are meant to be committed. TokenLedger writes only what passes through your
redactors (`Ledger.Options.Redact`); review recordings like any other file before committing them.

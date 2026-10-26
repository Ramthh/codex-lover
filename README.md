# Codex Lover for Windows

[![Windows checks](https://github.com/Ramthh/codex-lover/actions/workflows/windows.yml/badge.svg)](https://github.com/Ramthh/codex-lover/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A Windows desktop companion for viewing Codex account usage and reset times,
organizing explicitly saved accounts, and requesting a guarded account switch.
Built with C#, WPF and .NET 10.

**Independent community software. Not affiliated with or endorsed by OpenAI.**

## Status and scope

This is the first public source snapshot, based on desktop version **1.7.16**.
It includes the desktop application, core library, regression tests, fake App Server,
and installer/packaging tools. It does not include the separately operated
codexlover.com website, billing backend, credentials, customer data, or old private
development history. Earlier downloadable installers are separate artifacts and
must not be assumed to have been built from this snapshot.

The source is MIT-licensed. Building, testing and the synthetic demo require no
membership. **Normal account monitoring currently requires an online Codex Lover
membership** and access to the official Codex services. Hosted Free/Pro membership
terms are separate from the source license. This release is not a fully self-hosted
service. See [service dependencies](docs/SERVICE-DEPENDENCIES.md).

## Features

- Account usage windows, reset times, freshness and authentication state.
- User-enrolled account profiles with isolated Codex homes and Windows access controls.
- Explicit account switching with process checks, identity checks and protected recovery.
- Tray operation, notifications, appearance settings and optional Windows startup.
- Website membership, news and update notices, with an isolated synthetic demo.
- Regression coverage for account isolation, recovery, redaction, membership and UI behavior.

Quota comes from the service. This app does not add quota, pool allowances, bypass
service limits, transfer running tasks, or automatically rotate accounts.

## Build and test

Use **Windows 11**, PowerShell 7, Git and the **.NET SDK pinned in global.json**
(10.0.400, latest servicing patch allowed). Ordinary development requires neither
a publisher certificate nor the Codex application. Internet access is needed to
restore NuGet dependencies on the first build.

~~~powershell
git clone https://github.com/Ramthh/codex-lover.git
cd codex-lover
dotnet restore .\CodexAccountMonitor.sln --locked-mode
dotnet build .\CodexAccountMonitor.sln -c Release --no-restore
dotnet test .\CodexAccountMonitor.sln -c Release --no-build --no-restore
dotnet run --project .\src\CodexAccountMonitor -c Release --no-build -- --demo
~~~

Demo mode uses synthetic example.com accounts and a separate temporary data root.
It does not sign in, switch real accounts or consume resets. Close the demo window
when finished. To use real accounts, start without --demo and follow the membership
and Codex sign-in flows. Finish active tasks before an account switch.

The complete validation helpers are:

~~~powershell
.\scripts\Test.ps1
.\scripts\Test.ps1 -Store
~~~

Build caches go to LocalAppData, outside the checkout. Test.ps1 uses separate
per-run caches and test-result directories. Pass -DotNetPath for a private SDK.
Store tests use their own checked dependency locks. Neither test mode signs in,
uses a paid reset, installs the app or quits your real Codex session.

## Downloads and compatibility

Existing official downloads: [codexlover.com/download](https://codexlover.com/download)
and [installer releases](https://github.com/Ramthh/codex-lover-releases/releases).
Their version and signing notices apply to those binaries, not automatically to this source.

This snapshot's packaging manifest pins Codex 0.155.1. Source builds need a
supported official Codex executable for real monitoring; a separately installed
desktop app is needed for desktop switching. Compatibility with newer Codex builds
must be tested explicitly. Automated tests use a fake App Server and do not prove
current live-service compatibility or Microsoft Store certification.

See [packaging](packaging/README.md), [user guide](docs/USER-GUIDE.md),
[architecture](docs/ARCHITECTURE.md), and [security policy](SECURITY.md).

## Contribute

[Contribution guide](CONTRIBUTING.md) · [Roadmap](docs/ROADMAP.md) ·
[Report a bug](https://github.com/Ramthh/codex-lover/issues/new/choose) ·
[Report a vulnerability privately](https://github.com/Ramthh/codex-lover/security/advisories/new)

The maintainer is [Ramthh](https://github.com/Ramthh). Useful contributions include
clear installation documentation, accessibility fixes, compatibility reports using
synthetic data, and regression fixes. Never attach auth.json, tokens, cookies,
membership sessions, or account databases to an issue.

## License

Original project source is available under the [MIT License](LICENSE).
External components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md).
The MIT license does not grant access to the hosted service, subscriptions, or a
right to imply endorsement by OpenAI or the original publisher.

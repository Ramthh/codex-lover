# Store validation scope

Store packaging is maintainer tooling for the original publisher identity.
The public source release does not claim a new Microsoft certification or a
fresh installed-package account-switch test.

Before distributing a binary:

- Run desktop and Store regression suites with locked restore.
- Build a fresh package and verify its exact SHA-256 with Test-StorePackageContents.ps1.
- Preserve upstream license notices and verify publisher signatures.
- Test install, upgrade, uninstall, launch and recovery in a disposable Windows environment.
- Check the exact supported Codex version using authorized test accounts.
- Review website membership availability separately from client code.

The lifecycle helpers deliberately require a Windows Sandbox test user and
synthetic fixtures. Do not run authenticated switching or installation checks
against a contributor's active Codex session. Retain release evidence privately;
only publish sanitized summaries tied to an exact public commit.

# Contributing

Thank you for improving Codex Lover. Start with the build and demo commands in
[README.md](README.md), then run scripts/Test.ps1. Run the Store variant when
changing platform conditionals, packaging or dependencies.

## Workflow

1. Search existing issues. For a substantial change, describe the problem and intended behavior first.
2. Fork the repository and create a focused branch.
3. Make the smallest useful change. Add regression coverage for changed behavior.
4. Run relevant checks; include commands, results and remaining limitations in the pull request.
5. Submit a pull request explaining the user-visible effect.

Use synthetic fixtures and the fake App Server. Tests must not touch real
credentials, installed apps, paid reset credits, production memberships or billing.
Do not force-close Codex. Preserve identity checks, file-change guards, bounded
timeouts, signing validation and confirmation before external actions.

## Dependencies and generated files

NuGet lockfiles are checked in. Use locked restore for normal work. A dependency
change must intentionally update and review both normal and Store dependency
graphs. Do not commit build caches, binaries, private keys, diagnostics, user data,
auth.json, .env files or production configuration. The exact fake key in
SafeJsonLoggerTests has a single-line scanner exception because it tests redaction;
do not expand that exception to an entire directory.

Use PowerShell 7 for repository scripts. Optional Store asset generation uses
the locked Node dependencies in packaging; see packaging/README.md.

## Community and ownership

Be respectful, explain disagreements concretely, and avoid publishing private
information. Contributions must be yours to submit or clearly attributed with
compatible licensing. Contributions to original project files are submitted under
this repository's MIT license. Keep third-party notices intact.

This is an early public project with one maintainer. There is no guaranteed review
or support response time. Security reports belong in the private reporting channel
in SECURITY.md, not public issues.

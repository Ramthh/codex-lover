> Maintainer design notes retained from development. Version-specific release
> observations are historical; see the root README for the public snapshot and
> service dependencies. These notes are not a new independent audit.

# Short security review

Review date: 2026-09-01

Scope: the .NET 8 WPF solution, Core policies, Windows persistence/integration services, local Codex App Server client, and publish documentation. This is a source review, not a penetration test or a review of the Codex service.

## Summary

The architecture has a sound least-surprise boundary for a local monitor: enrollment is explicit, each child App Server receives a process-local isolated home, refresh concurrency is bounded, the UI does not rotate quotas, and switching verifies identity without transferring a running turn. Secret handling is materially safer than plaintext configuration because the optional API key uses Windows Credential Manager and protocol/stderr content is not logged.

The current build should still be treated as a release candidate until protocol-version compatibility, signing, clean-machine packaging, and end-to-end negative tests are completed.

## Findings

| Severity | Finding | Current state / recommendation |
| --- | --- | --- |
| Medium | App Server schemas are supplied by the installed Codex build and can evolve independently of this application. | Missing fields are mostly handled conservatively. Add a supported-version matrix and run contract tests against generated schemas for every release. |
| Medium | API fallback UI/key storage exists without an API task runner or provider usage reconciliation. | Clearly label configuration-only behavior. Do not market fallback as operational. Any future runner needs atomic reservations, retry reconciliation, and an external billing-limit disclaimer. |
| Low | Codex executable validation uses `WinVerifyTrust` with cache-only URL retrieval and does not request live revocation checking. | Configured and discovered paths now receive the same Authenticode/OpenAI-signer validation. Define and test the intended offline and revocation policy before release. |
| Low | Root metadata/settings/log directories rely on the Windows profile ACL, while individual account directories receive an explicit protected DACL. | Consider explicitly hardening the entire application root and verifying inherited ACLs during upgrades. |
| Low | Log redaction recognizes sensitive field names and common token formats but cannot prove arbitrary future values are non-secret. | Keep raw protocol, stderr, environment values, auth URLs, user codes, and exception messages out of logging. Add regression tests with representative secret formats. |
| Low | Removing a profile uses normal recursive deletion. | Root and nested reparse points are rejected. A durable tombstone retries failed cleanup on startup, but deletion is still not cryptographic secure erasure. |
| Low | Startup uses the current-user `Run` key and packaging behavior is not verified. | Re-test under MSIX; use a supported startup-task declaration if packaging requires it. |
| Release | No publisher signature, installer identity, update channel, SBOM, or clean-machine install/uninstall evidence is present. | Sign the executable/package, protect signing keys, generate an SBOM, scan dependencies, and retain checksum/test evidence before public release. |

## Positive controls observed

- Per-account GUID paths and process-only `CODEX_HOME` / `CODEX_SQLITE_HOME` environment changes.
- Explicit current-user DACL on profile data.
- Existing profile trees have reparse points rejected and their current-user-only ACL reapplied before each App Server start.
- Identity verification for refresh and switch.
- Last-known-good quota preservation on errors, including 429 handling.
- Two-account refresh semaphore and serialized mutations/switches.
- Reset-credit confirmation plus persisted idempotency state.
- No browser-cookie scraping, password collection, global home mutation, account pooling, or automatic rotation.
- Local stdio transport rather than an exposed App Server listener.
- Configured and discovered Codex executable paths both require a valid Windows Authenticode result and an OpenAI signer identity.
- Windows Credential Manager for the optional API key.
- Structured logger excludes exception messages and App Server stderr.
- Directory deletion is scoped by the account-home path policy.
- Credential-directory deletion failures retain a durable retry tombstone.
- App Servers are attached to Windows kill-on-close Job Objects, and resident crash recovery uses exponential backoff plus a circuit breaker.

## Release security checklist

- [ ] All tests pass on Windows 11 x64; repeat on Arm64 if shipped.
- [ ] Test a clean Windows user profile with no existing Codex or .NET SDK state.
- [ ] Test the minimum and selected supported official Codex versions.
- [ ] Confirm no credentials appear in logs during successful login, failed login, RPC failure, reset redemption, and shutdown.
- [ ] Confirm a profile cannot read another profile or start with a mismatched reported `codexHome`.
- [ ] Confirm a running managed turn cannot be killed, switched, or transferred.
- [ ] Verify configured/discovered executable signature, timestamp, offline-cache, and revocation-policy behavior and error messaging.
- [ ] Run dependency/vulnerability scanning and generate an SBOM.
- [ ] Authenticode-sign binaries and sign the installer/MSIX with the intended publisher identity.
- [ ] Verify install, upgrade, uninstall, start-with-Windows, Credential Manager, and retained-data behavior.
- [ ] Publish a private vulnerability reporting channel and a version support policy.

## Not assessed

- OpenAI service-side authentication, billing, quota calculations, or reset-credit semantics.
- Browser and device-code implementation outside the process.
- Resistance to administrator or same-user malware.
- Enterprise deployment policies, proxies, endpoint security products, accessibility certification, or store submission.
- A future API task runner, automatic updater, telemetry service, or crash reporter.

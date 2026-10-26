# Security policy

The public source baseline is 1.7.16. Report against the current main branch or a
precise public commit. Earlier release-specific observations below are historical;
automated checks are not a live-service or independent security certification.


## Supported versions

Security fixes target the current public main branch. The initial snapshot is based on 1.7.16; older downloadable installers have separate provenance and support status. This source publication does not establish a new signed release, live OAuth/payment verification, or a long-term support commitment.

## Reporting a vulnerability

Do not include access tokens, API keys, cookies, passwords, full `codex-home` directories, or unredacted account data in a public issue.

Use [GitHub private vulnerability reporting](https://github.com/Ramthh/codex-lover/security/advisories/new). If that page is unavailable, use the private contact form at https://codexlover.com/#contact with a minimal non-secret summary and request a secure follow-up. Do not post exploit details or private account data in a public issue. No response-time guarantee is offered.

Include:

- Affected version or commit.
- Windows version and architecture.
- Codex version from `codex --version`.
- Impact and prerequisites.
- Minimal reproduction steps using test accounts and synthetic data.
- Whether the issue can expose credentials, cross profile boundaries, run an unexpected executable, consume a reset credit, or interrupt a managed turn.

## Security scope

High-priority issues include:

- Reading, writing, or launching with the wrong saved profile's `CODEX_HOME`.
- Changing desktop credentials without the explicit switch action, or writing outside the supported local Codex `auth.json` and protected recovery/profile paths.
- Tokens, API keys, cookies, user codes, or passwords appearing in logs or plaintext settings.
- Bypassing Windows Authenticode validation or the OpenAI signer check for a configured/discovered Codex executable.
- Switching identity during a running monitor-managed turn.
- Bypassing desktop/CLI closed-process checks, overwriting independently changed credentials during rollback, or rotating a refresh token concurrently from a linked desktop and monitor session.
- Saving stale desktop credentials over a profile that has been reauthenticated since the recorded switch.
- Reset-credit consumption without an explicit confirmation or repeat-safe idempotency behavior.
- Directory traversal, junction/reparse-point abuse, or deletion outside the selected account directory.
- Bypassing the configured API spending guard if an API task runner is added later.

Visual defects, incorrect aliases, service-reported quota differences, or unsupported provider/account types are ordinary bugs unless they create a security impact.

## Trust model

The application trusts:

- The current interactive Windows user.
- Windows user-profile ACLs and Credential Manager.
- Current-user Windows DPAPI for desktop-switch recovery records.
- The official Codex executable selected or discovered by the user.
- The local browser used for the Codex-managed authentication ceremony.
- The Codex service and the App Server responses returned for the signed-in account.

The design does not defend against an administrator, same-user malware, a compromised Codex binary, a compromised browser session, or a compromised Windows account. Per-profile DACLs isolate accidental cross-profile use; they are not a sandbox from processes running as the same Windows identity.

## Implemented controls

- Normal startup requires successful website membership verification. The desktop has no shipped license bypass or embedded Pro entitlement. Isolated, labeled demo mode cannot read or switch real Codex accounts.
- Website authorization uses one-time device codes bound to an S256 PKCE challenge. Opaque membership sessions are stored only as CurrentUser DPAPI ciphertext. Authentication responses/tokens are never logged.
- Website links are constrained to HTTPS `codexlover.com` without custom ports or user information; the production HTTP client does not follow redirects. Responses are size-bounded.
- Membership revalidates every two minutes, fails closed on failed verification, and expires cached validation after five minutes using monotonic elapsed time. Trial/Pro expiry and saved-account limits are checked before protected operations. A desktop client cannot defend against local binary modification; enforcement of issued entitlements and payments belongs on the website server.
- No local Codex credentials, profile email addresses, usage meters or Codex refresh tokens are sent to the website. Membership identity and Codex identity remain separate. Public article requests have no bearer token.

- Every monitor profile is assigned an opaque GUID and a process-local `CODEX_HOME`; environment changes apply only to child App Servers. An explicit desktop switch can replace the existing local desktop home's `auth.json` while Codex and other Codex CLI sessions are closed. It does not change the global environment or Codex configuration.
- Account directories receive a reapplied current-user-only DACL before App Server launch, reparse points are rejected, and deletion is constrained below the accounts root.
- Enrollment is explicit and uses Codex-managed browser or device-code login. The widget never asks for a password or scrapes cookies.
- Account identity is verified through `account/read` before enrollment and switching. Desktop switching also verifies a service usage response before installing a different saved login and checks identity consistency before and after the auth-file replacement.
- Desktop switching supports local file-based ChatGPT credentials. Configured keyring/`auto` storage, an incompatible forced login method, or an incompatible required workspace causes a refusal without changing configuration.
- The previous desktop login and switch state are protected with current-user DPAPI. Atomic auth-file replacement and recovery use closed-process checks and expected-file comparisons; independent changes are not overwritten.
- The linked desktop account is monitored through a temporary isolated access-token-only App Server. Its refresh token is not supplied to the monitoring session. A receipt's original source credential hash prevents stale saveback after profile reauthentication.
- A verified OpenAI-signed desktop executable is checked before launch. The monitor never force-closes desktop or CLI processes to make a switch succeed.
- A legacy API key, if present, remains in Windows Credential Manager with only a boolean presence flag in JSON. The current UI permits removal and does not implement API fallback execution.
- Structured logs use allowlisted primitive values, redact known sensitive field names and common token patterns, omit exception messages, and drain App Server stderr without recording it.
- Reset-credit redemption requires confirmation and persists a per-attempt idempotency key.
- The internal resident-session coordinator refuses to stop a monitor-owned session with a running turn and handles idle transitions separately from desktop selection. Desktop switching locks enrolled profiles and refuses to interrupt managed work.
- No network listener is opened by the widget; App Server uses local redirected standard input/output.
- Every owned App Server is attached to a Windows kill-on-close Job Object. Unexpected resident exits use bounded exponential-backoff recovery with a circuit breaker.
- Profile removal records a durable cleanup tombstone so a failed credential-directory deletion is retried at startup.

## Residual risks and operator responsibilities

- Configured and automatically discovered Codex executables must pass `WinVerifyTrust` and an OpenAI signer-name check. The current verification uses cache-only URL retrieval and does not request live revocation checking; publishers should define and test the intended offline/revocation policy for their threat model.
- The redaction layer is defense in depth, not a proof that arbitrary future secret formats cannot reach a log. Never add raw protocol messages, process environment values, App Server stderr, or exception messages to logging.
- `accounts.json`, `settings.json`, reset-operation state, and logs rely on the Windows user profile's normal ACL. The per-account directory receives the additional explicit DACL.
- Codex authentication state exists inside each ACL-protected `codex-home`. Removing a profile uses normal deletion and may leave recoverable storage remnants even after the durable cleanup retry succeeds.
- A desktop switch deliberately changes the local sign-in used by Codex and CLI clients sharing that home. Credentials remain in Codex's normal auth file; DPAPI protects the monitor's recovery copy, not the Codex auth file itself. Profile removal does not sign out the desktop.
- **Selected** proves a match to the linked saved login and receipt, not the identity in a running process's memory. Users must quit Codex before switching and verify the account displayed after it reopens.
- Process enumeration and file guards reduce races but do not defend against another same-user process deliberately starting or changing credentials between checks. The supported workflow requires other Codex clients to remain closed until the switch finishes.
- App Server protocol and response schemas can change with the installed Codex version. Treat malformed or missing data as unavailable, never as zero remaining quota.
- The API spending limit is not a provider-enforced budget. A future API runner must atomically reserve/record cost, handle retries, and reconcile actual provider usage.
- Reset-credit use is an external side effect. Retrying must reuse the durable idempotency key until the result is certain.
- Release artifacts require Authenticode/MSIX signing, protected signing keys, dependency review, and clean-machine verification by the publisher.
- A new source release requires separate live-service and clean-machine verification before a corresponding binary is described as tested for current Codex versions. Automated source checks do not close a contributor's active Codex session or establish a live account-switch result.

See [docs/SECURITY-REVIEW.md](docs/SECURITY-REVIEW.md) for the current review findings and release recommendations.

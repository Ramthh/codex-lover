> Maintainer design notes retained from development. Version-specific release
> observations are historical; see the root README for the public snapshot and
> service dependencies. These notes are not a new independent audit.

# Architecture and protocol notes

## Purpose and boundary

Codex Lover Desktop is a WPF client that presents connected profiles plus one next add-account surface, backed by stable positive slot IDs. Pro has no fixed product cap; the trial operates the first two saved profiles. It monitors accounts through local Codex App Servers over redirected standard input/output and owns only the App Server processes it creates.

Version 1.5.0 adds a mandatory website membership gate before any normal monitor startup. `MembershipService` uses fixed HTTPS endpoints, browser device authorization with S256 PKCE, opaque session tokens protected by CurrentUser DPAPI, and online entitlement validation. The service receives only website identity and licensing data; it never receives local Codex credentials, enrolled email addresses or usage. See `MEMBERSHIP-INTEGRATION.md` for contracts. `AccountMonitorService` and `DesktopAccountSwitchService` enforce the gate before protected operations, including a second enrollment check inside the mutation lock. `NewsTicker` renders validated public article links; the public news request carries no membership token.

Version 1.4.0 adds an explicit desktop-account switch. After the user quits Codex and other Codex CLI sessions, the monitor verifies a selected saved account, replaces only the existing local Codex home's `auth.json`, and launches a verified signed Codex desktop executable. This is not an attachment to a running desktop process or a hot in-place account change. The separate ChatGPT application's sign-in is not changed.

The boundary is intentionally narrow:

```text
WPF dashboard / settings
          |
          v
DashboardViewModel and policies
          |
          +--> account repository and settings in the private per-user app data store
          |
          +--> per-profile App Server client (stdio JSONL)
                         |
                         +--> child codex.exe with process-local CODEX_HOME
                         |
                         +--> Codex-managed ChatGPT authentication and service calls

Switch dialog
      |
      +--> closed-process and signed-installation checks
      +--> selected-account service verification under profile locks
      +--> local Codex auth.json replacement + current-user DPAPI recovery journal
      +--> launch verified Codex desktop executable
```

No local TCP or WebSocket port is opened.

## Projects

- `CodexAccountMonitor.Core` contains account/quota models, reset formatting, state reduction, profile-switch state transitions, isolation path validation, data freshness, and API-spending policy.
- `CodexAccountMonitor` contains WPF views/view models and Windows-specific services for App Server processes, filesystem ACLs, Credential Manager, the tray, registry startup, logging, and single-instance activation.

The UI depends on Core, while Core has no WPF dependency. This keeps mapping and state-transition behavior unit-testable.

## Account lifecycle

### Enrollment

1. The user chooses the single **Add account** card (or the equivalent settings action) and supplies a local alias. The action targets the first free stable slot ID.
2. The application creates an opaque profile ID and an `accounts\<id>\codex-home` directory inside its private app data store.
3. The profile directory is restricted to the current Windows user's SID and configured to keep Codex auth inside that home.
4. `codex app-server --listen stdio://` starts with `CODEX_HOME` and `CODEX_SQLITE_HOME` set only in the child environment.
5. The client performs `initialize` then sends `initialized`.
6. `account/login/start` begins a browser or device-code flow owned by Codex. The application opens the returned URL and, for device flow, shows the returned user code.
7. After `account/login/completed`, `account/read` must return a ChatGPT identity.
8. Only a masked email and one-way fingerprint are persisted with the alias. Codex-managed auth remains in the isolated home.

An incomplete enrollment deletes the newly created isolated directory and does not add a profile.

The view model creates stable card instances on demand and exposes configured cards plus the first free slot through an observable collection. A successful enrollment grows the visible collection by one configured card and one next add card. Removing a profile exposes the first free slot again. Repository validation permits positive unique slots beyond ten; refresh-all uses a two-worker parallel loop rather than one unbounded task per account.

### Refresh

1. The scheduler waits for the configured interval plus random jitter.
2. It serializes each profile's read/reduce/persist transaction so overlapping manual, scheduled, and notification refreshes cannot overwrite newer state.
3. A verified monitor-owned resident profile can reuse its App Server. Other profiles use short-lived App Servers with a maximum of two running concurrently. For the account linked to the desktop sign-in, `DesktopCredentialRouter` creates an isolated temporary App Server with only the current desktop access token; it does not lend the refresh token to that session.
4. `account/read` must match the enrollment fingerprint.
5. `account/rateLimits/read` is mapped into service-reported short-term and long-term windows from one authoritative meter. The backward-compatible top-level account-wide bucket is retained alongside the named-meter map.
6. A successful snapshot becomes current. A failure preserves the prior good snapshot, records a safe error code, and updates freshness/authentication state.

For the resident profile, `account/rateLimits/updated` is only a debounced refresh signal; the card is updated from a subsequent authoritative `account/read` plus `account/rateLimits/read`. Authentication or identity failure quarantines the resident session and clears its active presentation. If a managed turn is running, ownership is retained and the re-verification restart waits for idle rather than interrupting the turn.

Missing windows remain missing. A 429 does not become `0% remaining`.

For a linked desktop account, the router checks both the saved identity and the source credential hash recorded in the protected switch receipt. The receipt's provenance prevents a later reauthentication from being overwritten by an older desktop credential copy. If the linked sign-in is missing, stale, or unverifiable, usage fails safely instead of falling back to a potentially competing refresh-token session. Temporary access-token sessions are disposed and their isolated staging directories removed after use.

### Desktop account switch

```text
Switch button opens dialog
      |
      v
user finishes tasks/saves edits; separate Codex CLI sessions must finish
      |
      v
Switch & reopen
      |
      +-- membership / supported-home / local profile check fails --> leave login unchanged
      |
      v
prepare monitor sessions for maintenance; lock enrolled profiles in stable order
      |
      v
request normal Quit from a verified desktop window; wait for every Codex blocker
      |
      +-- unknown process / external CLI / refused quit / timeout --> no sign-in change
      |
      v
verify selected saved account identity and service usage response
      |
      +-- verification fails --> preserve current desktop login
      |
      v
recheck closed processes; protect previous auth in current-user DPAPI journal
      |
      v
replace only auth.json; verify written identity; finalize protected receipt
      |
      v
recheck closed processes; launch verified signed Codex desktop
```

`DesktopAccountSwitchService` serializes switches. `WindowsCodexDesktopAdapter` inspects Codex-related processes, excludes verifiable monitor-owned helpers, and treats unavailable process information conservatively. A desktop launched by the monitor and all its descendants remain blockers. A monitor-managed running turn prevents maintenance that would interrupt it.

Package-helper detection is bounded to the verified desktop application directory. Cached tools in the package's user-data directory do not become Codex clients just because their path contains the package name. The independently registered `CodexSandboxService.OpenAI.Codex` Windows service can remain running: the adapter verifies its registered PID, configured package binary and OpenAI signature before treating it as system infrastructure. A matching process name alone is insufficient. The switcher never stops or reconfigures that service.

The automatic flow requests the Windows desktop's normal Quit accelerator only after verifying the target process, executable, creation time and foreground window. It never force-terminates a process. Windows Codex can hide its last window in the tray, so `WM_CLOSE` alone is insufficient. Its normal Quit path can stop active tasks; the dialog explicitly tells users to finish tasks and save edits first. The app cannot claim to detect every active turn or automatically save external editors. Unknown or standalone CLI processes block automatic switching before Quit is requested.

`DesktopHomePolicy` accepts the existing local home resolved from `CODEX_HOME`, or `%USERPROFILE%\.codex` when unset. `DesktopAuthFileStore` validates local paths, refuses unsafe reparse paths, bounds credential input, and parses identity consistency before writing. The switch supports file-based ChatGPT credentials. Configured `keyring`/`auto` backends, incompatible forced login methods, and incompatible required workspaces are refused; the monitor does not edit Codex configuration to enable switching.

The auth-file replacement is transactional and protected by closed-process checks. Recovery compares the current file against the expected operation state and refuses to overwrite independent credential changes. Recovery records contain the prior login only under current-user Windows DPAPI protection. Retrying after an interrupted switch requires Codex to be closed.

The **Selected** indicator comes from a matching saved desktop login, linked profile identity, and receipt provenance. It is not proof of the identity loaded by a running Codex process. Users verify Codex's account display after reopening. New requests use the selected account's quota; existing running work is never transferred. CLI clients sharing the same Codex home also use the changed sign-in.

The monitor's existing internal resident-session coordinator remains separate from desktop selection. It manages monitor-owned sessions and idle transitions; its active-profile state is not used as evidence of the desktop account.

## Quota mapping

The source of truth is the service response, not the card labels. The mapper:

- Treats every `limitId` as an independent metered bucket and never combines windows across buckets.
- Treats the top-level `rateLimits` object as the backward-compatible account-wide view and gives it precedence over named/model-specific meters. A matching map entry may fill missing fields only when its `limitId` is the same.
- On older responses without that top-level view, prefers the case-insensitive canonical `limitId == "codex"` bucket. If no usable canonical bucket exists, a single usable legacy/noncanonical bucket may be displayed; multiple noncanonical buckets remain ambiguous.
- Uses `windowDurationMins` as data rather than assuming every plan reports exactly five-hour and seven-day windows. Exact 5h and 7d periods retain familiar labels; other service-reported periods receive accurate dynamic labels such as `30d left`.
- Calculates displayed remaining percent as `100 - usedPercent`, clamped to the display range; `usedPercent` itself is consumed quota.
- Parses both `rateLimits` and `rateLimitsByLimitId` when both representations are present.
- Preserves `resetsAt` as an absolute instant and formats it in local time at the UI boundary.
- Uses `rateLimitResetCredits.availableCount` as authoritative for the reset bank.
- Leaves absent or ambiguous windows as unavailable instead of synthesizing a value.

## Desktop widget presentation

The main window is a borderless per-pixel-transparent WPF surface with translucent shell and card brushes. A persisted `WidgetOpacityPercent` setting adjusts those dynamic brushes without fading text or controls. Opaque and Windows high-contrast modes use solid surfaces. With **Minimize to tray** enabled, the window uses the Windows tool-window style and remains available from the notification-area icon; header close and minimize hide it. With that preference disabled, minimize uses the taskbar and close exits, subject to the running-work guard. The tray **Exit** action also checks for managed work.

Context menus, menu rows, separators, tooltips, combo boxes, and dropdown items use shared WPF templates with opaque popup surfaces. Normal, highlighted, selected, and disabled foreground/background pairs are explicit and map to dynamic system colors in high-contrast mode. Child text inherits its control's foreground so a selected item can use the corresponding contrasting text color. Native control classes and popup parts retain keyboard navigation and selection behavior.

The default compact width is 588 device-independent pixels and presents one account column. The header layout action toggles an expanded width of up to 1080 pixels; the responsive panel then presents two columns when at least two 500-pixel card widths plus spacing fit. Height follows the visible dynamic account collection, capped to the monitor work area with vertical scrolling when needed. Placement and layout changes are clamped to the current monitor's work area.

Topmost behavior is independent of the tray/tool-window style. `AlwaysOnTop` defaults to `false`, and existing settings from the earlier widget experience are migrated once to that safer default. Normal application windows can therefore cover the widget unless the user explicitly enables **Keep above other windows**. Click-through is also optional and uses the transparent extended-window style; the tray menu remains the recovery path when pointer input is disabled.

## App Server protocol compatibility

The official [Codex App Server documentation](https://learn.chatgpt.com/docs/app-server) defines App Server as the deep-client integration surface and documents stdio as newline-delimited JSON. It also requires one initialization handshake per connection and documents the account, login, rate-limit, notification, and reset-credit methods used here.

Important compatibility rules:

- Use the official installed Codex executable and test the exact release version.
- Do not log raw requests, responses, environment variables, auth URLs, user codes, stderr, or RPC error messages.
- Reject an initialized App Server that reports a different Codex home.
- Bound protocol line length and request timeouts.
- Treat unknown fields as forward-compatible and missing required meaning as unavailable.
- Preserve the last good snapshot on transient failures.
- Generate schemas with `codex app-server generate-json-schema` during compatibility testing; generated schemas are version-specific.

The widget uses the documented stdio transport. The official documentation describes App Server WebSocket transport as experimental/unsupported for production, and this application does not expose it.

## Persistence

From 1.7.2, the default store is `%USERPROFILE%\.codex-lover\appdata\CodexAccountMonitor`. This location remains consistent when the monitor is launched from the packaged Codex environment or from Explorer. Startup migrates the effective legacy store into a new private copy and preserves the historical source folders. A completed-store marker distinguishes a valid copy from interrupted staging. Explicit test/demo roots remain isolated.

| Location | Contents | Secret? |
| --- | --- | --- |
| `accounts\<id>\codex-home` | Codex-managed per-account state and authentication | Yes; ACL-protected |
| `accounts.json` | Alias, masked identity metadata, usage snapshot | No raw credential; private metadata |
| `settings.json` | UI and refresh settings, active profile ID, API-key presence flag | No API key |
| `reset-operations.json` | Reset-credit idempotency state | No credential; operation metadata |
| `pending-account-deletions.json` | Durable retry list for credential-directory cleanup | No credential; opaque profile IDs |
| `desktop-switch\desktop-switch.pending.dpapi` | Interrupted auth-file replacement recovery record | Yes; current-user DPAPI-protected |
| `desktop-switch\desktop-switch.receipt.dpapi` | Completed switch receipt, source credential provenance, and previous-login recovery state | Yes; current-user DPAPI-protected |
| `logs\*.jsonl` | Sanitized structured events | Intended non-secret; review before sharing |
| Windows Credential Manager | Legacy API key, if saved by an earlier build; removal only | Yes |
| Existing local Codex home `auth.json` | The login selected for desktop Codex and CLI clients sharing that home | Yes; Codex's credential file |

Settings and account metadata are saved through a temporary file followed by replacement. Profile creation and removal are constrained through the account-home path policy. Removal writes a durable cleanup tombstone before committing metadata deletion; a failed directory deletion is retried on the next start. Removing a saved monitor profile does not remove the selected desktop login. Switching leaves projects, Codex configuration, global environment settings, and the monitor's Windows startup preference in place.

## Concurrency and failure behavior

- Launch ownership: the monitor must run independently of Codex before requesting its normal Quit. An unsafe inherited lifetime triggers a bounded Explorer handoff, with a one-shot child verification and no scheduled task or persistent service.
- Switch diagnostics: each attempt records a random correlation ID, monitor PID, fixed stage and fixed failure category. Checkpoints are flushed before external actions; credentials and raw exception messages are excluded.
- Account refresh: ordered per profile; maximum two short-lived App Server processes concurrently.
- Account/profile mutation: serialized through a semaphore.
- Monitor-owned session switch: serialized and guarded against a running managed turn.
- Desktop account switch: serialized, stable-order profile locks, selected-account service verification, closed-process checks at mutation/launch boundaries, and protected compare-and-restore recovery.
- App Server writes: serialized; responses are correlated by numeric request ID.
- Login: bounded wait; user cancellation tears down the attempt.
- Shutdown: a managed running turn blocks the tray exit action.
- Child-process ownership: every App Server is assigned to a Windows kill-on-close Job Object, preventing an orphan if the UI process is forcibly terminated.
- Unexpected resident exits: one recovery pump per profile, bounded attempts, exponential backoff, and a cooldown circuit breaker.
- Unexpected UI exceptions: safe structured event plus a credential-free message.

## Deliberately excluded behavior

- Browser-cookie discovery or authentication scraping.
- Hot-switching a running desktop process or force-closing desktop/CLI work.
- Changing the `CODEX_HOME` environment setting, Codex configuration, projects, or files outside the supported auth/recovery paths for a desktop switch.
- Account pooling, automatic quota rotation, or transferring running work between identities.
- Remote App Server exposure.
- Plaintext API keys.
- Claims of uninterrupted service.

The current executable also does not implement a general task runner, provider-billed fallback execution, smaller-model launch, or reset-aware queue. Those features require a separate design and security review before they can be presented as operational.

# Codex Account Monitor 1.2.0

## Fixed

- Preserves the account-wide `rateLimits` response even when named/model-specific meters are also returned. This prevents an unrelated zero-used model meter from appearing as a false **100% left** account value.
- Displays the real long-term quota period reported for each plan. Seven-day windows remain **Weekly left**; a 30-day plan now appears as **30d left**.
- Merges complementary data only when both protocol views identify the same meter. Independent model meters are never combined.
- Skips malformed named-meter entries without discarding a valid account-wide response.

## Improved

- Manual refresh now reports full success, partial failure, and missing long-term quota accurately.
- The dashboard re-reads the latest account collection when queued refresh notifications reach the UI, preventing an older multi-account update from repainting newer values.
- A failed refresh is visible on its account card while the last known good quota remains available.
- Unreported values use a subdued progress track instead of looking like an exhausted 0% allowance.
- Launching a newer side-by-side build updates an existing **Start with Windows** registration to that build without changing the user's opt-in choice.

## Verification

- Tested against the Codex App Server 0.153.1 response shape.
- Release tests: 109 passed, 0 failed.
- Existing profiles and credentials remain in `%LOCALAPPDATA%\CodexAccountMonitor`; this release does not move or rewrite authentication data during installation.

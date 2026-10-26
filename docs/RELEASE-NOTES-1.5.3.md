# 1.5.3 refresh recovery

- Fix simultaneous account refreshes incorrectly reporting that another account switch was in progress. Local readers and switch transactions now wait their turn before acquiring the existing exclusive file lock. Other processes remain excluded from an active transaction.
- Retry temporary usage-read connection and service failures up to twice, after 0.5 and 1.5 seconds. Another attempt is only eligible within ten seconds of the first attempt starting; each request retains its existing timeout. Each short-lived connection is disposed before reconnecting.
- Preserve the last successful quota and timestamp if all attempts fail. Report connection failures clearly and let the next scheduled refresh retry.
- Do not replay reset-credit consumption, sign-in, account switching, authentication failures, rate limits, invalid credentials, profile-isolation failures or cancelled operations.

Validation: the original lock race was reproduced with concurrent synthetic receipt reads. The refresh, account-operation, credential-store, routing and quota-state test selection passed all 92 tests, including regression coverage for retry recovery, exhaustion, cancellation, client disposal, transaction ordering and cross-process locking.

The account connection and membership fixes from [1.5.2](RELEASE-NOTES-1.5.2.md) are retained. Existing accounts and historical releases are preserved.

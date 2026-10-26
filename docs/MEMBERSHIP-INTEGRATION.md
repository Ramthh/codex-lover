> Maintainer design notes retained from development. Version-specific release
> observations are historical; see the root README for the public snapshot and
> service dependencies. These notes are not a new independent audit.

# Website / desktop contract

Production origin: `https://codexlover.com`. No configurable alternate production host is shipped. Browser authentication is separate from local Codex authentication. The desktop sends neither Codex credentials nor enrolled profile/usage data to these endpoints.

## Device authorization

1. Generate 32 random bytes, base64url without padding as `codeVerifier`. `codeChallenge` is base64url SHA-256 of its ASCII representation.
2. `POST /api/device/start` JSON `{provider:"google"|"twitter",codeChallenge}` returns `{deviceCode,verificationUri,expiresIn:600,interval:3}`. The browser URL must be exact HTTPS origin with path `/device`, e.g. `https://codexlover.com/device?code=...`.
3. Open that URL in the default browser. The server verifies user authorization/consent and binds the one-time code to the signed-in website user. The server must bind authorization to the original provider, enforce expiry, rate-limit attempts, and never put the polling secret in the browser URL.
4. Poll `POST /api/device/token` JSON `{deviceCode,codeVerifier}` no faster than three seconds. `202` means authorization pending; other non-success responses fail the attempt.
5. Success `200`: `{accessToken,expiresAt,user:{id,name,email},entitlement:{plan,active,maxAccounts,trialEndsAt,proExpiresAt}}`. Dates are ISO-8601 UTC strings (nullable entitlement dates). Token is opaque and revocable, not a Codex credential. Device code must be single-use and verifier checked before issuance.

The desktop saves only `{accessToken,expiresAt}` as DPAPI ciphertext in `membership\session.dpapi` inside its private app data store. From 1.7.2 the default root is `%USERPROFILE%\.codex-lover\appdata\CodexAccountMonitor`; the upgrade preserves existing encrypted sessions when consolidating the effective legacy store. It does not persist a trusted entitlement. Browser login does not require a custom URL scheme or localhost listener.

## Membership and billing

`GET /api/me` requires `Authorization: Bearer <opaque token>` and returns `{user,entitlement}` in the same shape. A 401 deletes the local session. A failed check immediately clears its validation freshness; no offline grace unlock is provided. Every normal start validates online; a periodic check runs every two minutes, with a five-minute monotonic validation lifetime protecting suspended/background operation.

Valid trial: `plan:"trial",active:true,maxAccounts:2,trialEndsAt:<future date>`. Valid Pro: `plan:"pro",active:true,maxAccounts:null,proExpiresAt:<future date>`. All other shapes/plans fail closed. Trial operates the first two currently saved slots and refuses new enrollment at two; extra historical credentials are retained. The website must enforce one trial per website account, server-clock expiry, and entitlement changes based on verified PayPal server events. Buying additional years should extend `proExpiresAt` from the later of current expiry or payment time.

Renewal and account management opens `/dashboard/billing` in the browser. The app does not perform PayPal checkout or accept a client-only checkout result. The member chooses Retry to immediately refresh after successful payment.

`POST /api/session/logout` with the bearer token revokes the current session. On successful/401 response, local session and membership state are cleared. A failed revocation is reported, not claimed successful.

## Published news

`GET /api/news` is public and returns `{articles:[{id,slug,title,excerpt,publishedAt,url}]}`. `url` is an absolute HTTPS article URL on exact `codexlover.com`. The desktop filters invalid/foreign links and overlong titles, keeps at most twenty articles, and refreshes every ten minutes. It retains the last fetched articles on transient errors. This request does not carry the member token.

The ticker loops through headlines with a slow left-to-right pass. Mouse hover and keyboard focus stop motion and align the current headline for reading. Enter/click opens an article; Space toggles pause. When Windows client-area animation is disabled, text remains static while headlines rotate.

## Before enabling downloads

- Configure Google and X OAuth credentials and production callback URLs; verify both providers using a browser and this app.
- Verify authorization consent, wrong PKCE rejection, device-code expiry/single use, 401 session revocation and cross-account protection.
- Verify trial begins only according to server policy, ends after three days, and refuses a third enrolled account; verify Pro unlocks more than ten profiles.
- Verify PayPal server-confirmed purchase, renewal extension, cancellation/refund policy, replay/idempotency protection and correct membership dates.
- Verify startup/network failure leaves the gate closed; sign-out/revocation blocks operations; local Codex auth remains unchanged by website login.
- Publish an article and verify the desktop ticker and article link.
- Sign the release with a trusted publisher certificate, then enable a measured download endpoint. Do not claim a production user/device/download count without recorded evidence.

The desktop cannot report exact installed/downloaded device counts without an explicitly designed privacy-preserving event. No such telemetry is added here; website authenticated download events can be tracked server-side when downloads are enabled.

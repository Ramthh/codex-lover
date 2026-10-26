# 1.5.2 account connection fix

- Fix account enrollment when Windows redirects the profile folder into the Codex package's LocalCache. Different path strings are accepted only when directory handles prove they refer to the same physical directory using its volume and full 128-bit file ID. A genuinely different profile, inaccessible directory or missing directory is still rejected.
- Keep an existing fresh membership usable while a background recheck is pending. Failed checks, cancellations, expired memberships and expired sessions still block access.
- Report membership, missing executable, Windows permissions, RPC and profile-isolation failures separately. Enrollment diagnostics record only the stage, exception type and numeric error codes, never sign-in URLs or credentials.

The automatic browser-to-desktop login handoff from 1.5.1 is retained. Historical releases and account data are preserved.
